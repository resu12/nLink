using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private static readonly TimeSpan FileTransferTerminalTombstoneRetention = TimeSpan.FromMinutes(2);

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

        if (!TryValidateAndTrackFileTransferMessage(MsgType.FileTransferSessionOpen, message.TransferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            NknRuntimeDiagnostics.SetLastError($"file_transfer_session_open_{failureReason}");
            Log($"SendFileTransferSessionOpenAsync failed (reason={failureReason}, transfer_id={message.TransferId})");
            throw new InvalidOperationException($"File-transfer state rejected message '{MsgType.FileTransferSessionOpen}': {failureReason}.");
        }

        await SendFileTransferEnvelopeRawAsync(
                MsgType.FileTransferSessionOpen,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                useBulkLane: false,
                frameType: null,
                ct: ct)
            .ConfigureAwait(false);

        if (!TryValidateAndTrackFileTransferMessage(MsgType.FileTransferSessionOpen, message.TransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={MapSecureFileTransferMessageType(MsgType.FileTransferSessionOpen)}; transfer_id={message.TransferId}; source={LocalPeerAddress}");
        }
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

        await SendFileTransferCancelEnvelopeAsync(
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

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
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

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
                MsgType.FileTransferComplete,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
                MsgType.FileTransferPauseControl,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
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
                MsgType.FileTransferHeartbeat,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
                MsgType.FileTransferTransportEpoch,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
                MsgType.FileTransferTransportProbe,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferRedundantLifecycleEnvelopeAsync(
                MsgType.FileTransferRepairProof,
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
            fileTransferDataSessionRemoteOpenSuppressed.Remove(normalizedTransferId);
            fileTransferDataSessions.TryGetValue(normalizedTransferId, out var existingSession);
            TransportFileTransferDataSession? session = existingSession;
            if (session is not null &&
                session.IsDisposed)
            {
                fileTransferDataSessions.Remove(normalizedTransferId);
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_session_recreated; transport=nkn; transfer_id={normalizedTransferId}; session_id={normalizedSessionId}; reason=disposed_existing_session_on_open");
                session = null;
            }

            if (session is null)
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

        if (request.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            throw new InvalidOperationException("Only V6 file-transfer data frames are supported.");
        }

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

    private async Task SendFileTransferRedundantLifecycleEnvelopeAsync(
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

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
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

        var controlPayload = CreateSecureFileTransferPayload(messageType, normalizedTransferId, plaintextPayload);
        var controlEnvelope = CreateEnvelope(envelopeCode, messageType, controlPayload, replyTo: null);
        var controlTask = SendFileTransferLifecycleCopyAsync(
            messageType,
            remoteEndpoint,
            controlEnvelope,
            useBulkLane: false,
            lane: "control",
            ct);

        Task<LifecycleCopySendResult>? bulkTask = null;
        if (!string.IsNullOrWhiteSpace(remoteBulkEndpoint))
        {
            var bulkPayload = CreateSecureFileTransferPayload(
                messageType,
                normalizedTransferId,
                plaintextPayload,
                useBulkIdentity: true);
            var bulkEnvelope = CreateEnvelope(envelopeCode, messageType, bulkPayload, replyTo: null);
            bulkTask = SendFileTransferLifecycleCopyAsync(
                messageType,
                remoteBulkEndpoint,
                bulkEnvelope,
                useBulkLane: true,
                lane: "bulk",
                ct);
        }

        var controlResult = await controlTask.ConfigureAwait(false);
        var bulkResult = bulkTask is null
            ? new LifecycleCopySendResult("bulk", false, null)
            : await bulkTask.ConfigureAwait(false);
        if (!controlResult.Succeeded && !bulkResult.Succeeded)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_lifecycle_redundant_both_failed; transport=nkn; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={normalizedTransferId}; control_error={controlResult.Error?.GetType().Name ?? "(none)"}; bulk_error={bulkResult.Error?.GetType().Name ?? "(none)"}");
            throw controlResult.Error ?? bulkResult.Error ?? new InvalidOperationException($"File-transfer lifecycle send failed on both lanes for '{messageType}'.");
        }

        if (!TryValidateAndTrackFileTransferMessage(messageType, normalizedTransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={normalizedTransferId}; source={LocalPeerAddress}");
        }

        LogFileTransferEnvelopeEvent("sent", messageType, normalizedTransferId, source: LocalPeerAddress);
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_lifecycle_redundant_sent; transport=nkn; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={normalizedTransferId}; control_sent={(controlResult.Succeeded ? 1 : 0)}; bulk_sent={(bulkResult.Succeeded ? 1 : 0)}");
    }

    private async Task<LifecycleCopySendResult> SendFileTransferLifecycleCopyAsync(
        MsgType messageType,
        string destination,
        Envelope envelope,
        bool useBulkLane,
        string lane,
        CancellationToken ct)
    {
        try
        {
            if (useBulkLane)
            {
                await SendBulkEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }
            else
            {
                await SendEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }

            return new LifecycleCopySendResult(lane, true, null);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_lifecycle_redundant_{lane}_failed; transport=nkn; message_type={MapSecureFileTransferMessageType(messageType)}; error={ex.GetType().Name}");
            return new LifecycleCopySendResult(lane, false, ex);
        }
    }

    private async Task SendFileTransferCancelEnvelopeAsync(
        string transferId,
        byte[] plaintextPayload,
        CancellationToken ct)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("file_transfer_cancel_no_session_context");
            Log("SendFileTransferCancelAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("file_transfer_cancel_no_remote_endpoint");
            Log("SendFileTransferCancelAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryValidateAndTrackFileTransferMessage(MsgType.FileTransferCancel, normalizedTransferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            NknRuntimeDiagnostics.SetLastError($"file_transfer_cancel_{failureReason}");
            Log($"SendFileTransferCancelAsync failed (reason={failureReason}, transfer_id={normalizedTransferId})");
            throw new InvalidOperationException($"File-transfer state rejected message '{MsgType.FileTransferCancel}': {failureReason}.");
        }

        var controlPayload = CreateSecureFileTransferPayload(MsgType.FileTransferCancel, normalizedTransferId, plaintextPayload);
        var controlEnvelope = CreateEnvelope(envelopeCode, MsgType.FileTransferCancel, controlPayload, replyTo: null);
        var controlTask = SendFileTransferCancelCopyAsync(
            remoteEndpoint,
            controlEnvelope,
            useBulkLane: false,
            lane: "control",
            ct);

        Task<CancelCopySendResult>? bulkTask = null;
        if (!string.IsNullOrWhiteSpace(remoteBulkEndpoint))
        {
            var bulkPayload = CreateSecureFileTransferPayload(
                MsgType.FileTransferCancel,
                normalizedTransferId,
                plaintextPayload,
                useBulkIdentity: true);
            var bulkEnvelope = CreateEnvelope(envelopeCode, MsgType.FileTransferCancel, bulkPayload, replyTo: null);
            bulkTask = SendFileTransferCancelCopyAsync(
                remoteBulkEndpoint,
                bulkEnvelope,
                useBulkLane: true,
                lane: "bulk",
                ct);
        }

        var controlResult = await controlTask.ConfigureAwait(false);
        var bulkResult = bulkTask is null
            ? new CancelCopySendResult("bulk", false, null)
            : await bulkTask.ConfigureAwait(false);
        if (!controlResult.Succeeded && !bulkResult.Succeeded)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_cancel_redundant_both_failed; transport=nkn; transfer_id={normalizedTransferId}; control_error={controlResult.Error?.GetType().Name ?? "(none)"}; bulk_error={bulkResult.Error?.GetType().Name ?? "(none)"}");
            throw controlResult.Error ?? bulkResult.Error ?? new InvalidOperationException("File-transfer cancel send failed on both lanes.");
        }

        if (!TryValidateAndTrackFileTransferMessage(MsgType.FileTransferCancel, normalizedTransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={MapSecureFileTransferMessageType(MsgType.FileTransferCancel)}; transfer_id={normalizedTransferId}; source={LocalPeerAddress}");
        }

        LogFileTransferEnvelopeEvent("sent", MsgType.FileTransferCancel, normalizedTransferId, source: LocalPeerAddress);
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_cancel_redundant_sent; transport=nkn; transfer_id={normalizedTransferId}; control_sent={(controlResult.Succeeded ? 1 : 0)}; bulk_sent={(bulkResult.Succeeded ? 1 : 0)}");
    }

    private async Task<CancelCopySendResult> SendFileTransferCancelCopyAsync(
        string destination,
        Envelope envelope,
        bool useBulkLane,
        string lane,
        CancellationToken ct)
    {
        try
        {
            if (useBulkLane)
            {
                await SendBulkEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }
            else
            {
                await SendEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }

            return new CancelCopySendResult(lane, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_cancel_redundant_{lane}_failed; transport=nkn; error={ex.GetType().Name}");
            return new CancelCopySendResult(lane, false, ex);
        }
    }

    private readonly record struct CancelCopySendResult(string Lane, bool Succeeded, Exception? Error);

    private readonly record struct LifecycleCopySendResult(string Lane, bool Succeeded, Exception? Error);

    private async Task SendFileTransferEnvelopeRawAsync(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        bool useBulkLane,
        string? frameType,
        CancellationToken ct,
        int rawPayloadBytes = 0,
        int batchChunkCount = 0,
        string? batchProfile = null,
        bool forceRegularNknBulk = false)
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

        var securePayload = CreateSecureFileTransferPayload(messageType, normalizedTransferId, plaintextPayload, useBulkIdentity: useBulkLane);
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
            rawPayloadBytes,
            batchChunkCount,
            batchProfile,
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
                rawPayloadBytes,
                batchChunkCount,
                batchProfile,
                rejected: true);
            throw new InvalidOperationException(
                $"Bridge payload too large for 'send' (max {FileTransferMaxBridgePayloadBytes} bytes).");
        }

        var sentViaAcceleration = false;
        if (useBulkLane)
        {
            sentViaAcceleration = await SendBulkEnvelopeAsync(
                    destination,
                    envelope,
                    transportPayload,
                    ct,
                    allowAcceleration: !forceRegularNknBulk)
                .ConfigureAwait(false);
        }
        else
        {
            await SendEnvelopeAsync(destination, envelope, transportPayload, ct).ConfigureAwait(false);
        }

        LogFileTransferEnvelopeEvent(
            "sent",
            messageType,
            normalizedTransferId,
            source: useBulkLane ? client.BulkAddress : LocalPeerAddress,
            effectiveTransport: sentViaAcceleration ? "tuna" : "nkn");
    }

    private bool DoesFileTransferDataFrameFitTransportBudget(
        string transferId,
        MsgType messageType,
        byte[] plaintextPayload,
        bool useBulkLane)
        => TryMeasureFileTransferDataFrameTransportBudget(
            transferId,
            messageType,
            plaintextPayload,
            useBulkLane,
            out var measurement) && measurement.Fits;

    private bool TryMeasureFileTransferDataFrameTransportBudget(
        string transferId,
        MsgType messageType,
        byte[] plaintextPayload,
        bool useBulkLane,
        out FileTransferTransportBudgetMeasurement measurement)
    {
        measurement = default;
        try
        {
            var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
            if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
            {
                return false;
            }

            var destination = useBulkLane ? remoteBulkEndpoint : remoteEndpoint;
            if (string.IsNullOrWhiteSpace(destination))
            {
                return false;
            }

            var securePayload = CreateSecureFileTransferPayloadForBudgetEstimate(
                messageType,
                normalizedTransferId,
                plaintextPayload,
                useBulkIdentity: useBulkLane);
            var envelope = new Envelope(
                Version: EnvelopeVersion,
                Code: envelopeCode,
                MessageId: Guid.NewGuid().ToString("N"),
                Type: messageType,
                Payload: securePayload,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: null);
            var transportPayload = EnvelopeCodec.Serialize(envelope);
            var bridgeCommandBytes = NknBridgePayloadAccounting.MeasureSendFrameBytes(destination ?? string.Empty, transportPayload);
            measurement = new FileTransferTransportBudgetMeasurement(
                plaintextPayload.Length,
                securePayload.Length,
                transportPayload.Length,
                bridgeCommandBytes,
                transportPayload.Length <= FileTransferMaxBridgePayloadBytes);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or CryptographicException or OverflowException)
        {
            return false;
        }
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
            rawChunkSize,
            1,
            ResolvePayloadEfficiencyProfileNameForDiagnostics(),
            rejected: transportPayload.Length > FileTransferMaxBridgePayloadBytes);
        return transportPayload.Length <= FileTransferMaxBridgePayloadBytes;
    }

    private static FileTransferDataFrame CreateChunkFrameForTransportBudgetEstimate(
        int negotiatedDataProtocolVersion,
        string sessionId,
        string transferId,
        int chunkIndex,
        int chunkCount,
        int rawChunkSize)
    {
        if (negotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            throw new InvalidOperationException("Only V6 file-transfer data frames are supported.");
        }

        return new FileTransferChunkBatchFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = chunkIndex,
            ChunkCount = 1,
            DataSegments = new[] { new byte[rawChunkSize] },
            BatchProfile = "v4_default_21k",
        };
    }

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

    private void SetFileTransferDataSessionsAvailability(
        bool isAvailable,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown)
    {
        var effectiveHandoffKind = ResolveFileTransferDataSessionAvailabilityHandoffKind(
            reason,
            handoffKind,
            targetTransport);
        TransportFileTransferDataSession[] sessions;
        int staleSessionCount;
        var explicitHandoff = requiresResumeRequest &&
                              effectiveHandoffKind != FileTransferTransportHandoffKind.None;
        lock (gate)
        {
            var staleTransferIds = fileTransferDataSessions
                .Where(static pair => pair.Value.IsDisposed)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleTransferId in staleTransferIds)
            {
                fileTransferDataSessions.Remove(staleTransferId);
            }

            staleSessionCount = staleTransferIds.Length;
            if (fileTransferDataSessions.Count == 0)
            {
                if (requiresResumeRequest || explicitHandoff)
                {
                    TryRecordPendingFileTransferV6HandoffLocked(
                        currentSessionSecurityState.SessionId?.Value,
                        reason,
                        effectiveHandoffKind,
                        targetTransport,
                        "availability_no_sessions");
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_data_session_availability_no_sessions; is_available={(isAvailable ? 1 : 0)}; reason={SanitizeLogToken(reason)}; requires_resume_request={(requiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(effectiveHandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; stale_session_count={staleSessionCount}");
                }

                return;
            }

            sessions = fileTransferDataSessions.Values.ToArray();
            var subscriberCount = sessions.Sum(static session => session.AvailabilitySubscriberCount);
            if (subscriberCount == 0 && (requiresResumeRequest || explicitHandoff))
            {
                TryRecordPendingFileTransferV6HandoffLocked(
                    currentSessionSecurityState.SessionId?.Value,
                    reason,
                    effectiveHandoffKind,
                    targetTransport,
                    "availability_no_subscribers");
            }
        }

        if (requiresResumeRequest || explicitHandoff)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_data_session_availability_broadcast; session_count={sessions.Length}; stale_session_count={staleSessionCount}; subscriber_count={sessions.Sum(static session => session.AvailabilitySubscriberCount)}; is_available={(isAvailable ? 1 : 0)}; reason={SanitizeLogToken(reason)}; requires_resume_request={(requiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(effectiveHandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}");
        }

        foreach (var session in sessions)
        {
            session.SetAvailability(isAvailable, reason, requiresResumeRequest, effectiveHandoffKind, targetTransport);
        }
    }

    private bool HasActiveFileTransferDataSessionsForRecovery()
    {
        lock (gate)
        {
            return fileTransferDataSessions.Any(static pair => !pair.Value.IsDisposed);
        }
    }

    private FileTransferTransportHandoffKind ResolveFileTransferDataSessionAvailabilityHandoffKind(
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (targetTransport != FileTransferTransportKind.RegularNkn ||
            handoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback)
        {
            return handoffKind;
        }

        var normalizedReason = SanitizeLogToken(reason);
        if (IsRegularNknRecoveryProbeToken(normalizedReason))
        {
            return FileTransferTransportHandoffKind.RegularNknRecovery;
        }

        if (string.Equals(normalizedReason, "transport_recovered_unproven", StringComparison.OrdinalIgnoreCase) &&
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var unresolvedEpoch) &&
            unresolvedEpoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
            unresolvedEpoch.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_availability_handoff_kind_preserved; session_id={SanitizeLogToken(unresolvedEpoch.SessionId)}; transfer_id={SanitizeLogToken(unresolvedEpoch.TransferId)}; direction={unresolvedEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={unresolvedEpoch.TransportEpoch}; reason={normalizedReason}; requested_handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; effective_handoff_kind={FormatFileTransferTransportHandoffKindForLog(FileTransferTransportHandoffKind.RegularNknRecovery)}");
            return FileTransferTransportHandoffKind.RegularNknRecovery;
        }

        return handoffKind;
    }

    private void RequestFileTransferDataSessionsHandoff(
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string? sessionId = null)
    {
        TransportFileTransferDataSession[] sessions;
        int staleSessionCount;
        int subscriberCount;
        var normalizedReason = SanitizeLogToken(reason);
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        lock (gate)
        {
            var staleTransferIds = fileTransferDataSessions
                .Where(static pair => pair.Value.IsDisposed)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleTransferId in staleTransferIds)
            {
                fileTransferDataSessions.Remove(staleTransferId);
            }

            staleSessionCount = staleTransferIds.Length;
            if (fileTransferDataSessions.Count == 0)
            {
                TryRecordPendingFileTransferV6HandoffLocked(
                    normalizedSessionId,
                    normalizedReason,
                    handoffKind,
                    targetTransport,
                    "no_sessions");
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=filetransfer_data_session_handoff_no_sessions; reason={normalizedReason}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; stale_session_count={staleSessionCount}");
                return;
            }

            sessions = fileTransferDataSessions.Values.ToArray();
            subscriberCount = sessions.Sum(static session => session.AvailabilitySubscriberCount);
            if (subscriberCount == 0)
            {
                TryRecordPendingFileTransferV6HandoffLocked(
                    normalizedSessionId,
                    normalizedReason,
                    handoffKind,
                    targetTransport,
                    "no_subscribers");
            }
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_data_session_handoff_broadcast; session_count={sessions.Length}; stale_session_count={staleSessionCount}; subscriber_count={subscriberCount}; reason={normalizedReason}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}");

        foreach (var session in sessions)
        {
            session.RequestHandoff(normalizedReason, handoffKind, targetTransport);
        }
    }

    private static bool ShouldPersistPendingFileTransferV6Handoff(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => (handoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
            targetTransport == FileTransferTransportKind.Tuna) ||
           (targetTransport == FileTransferTransportKind.RegularNkn &&
            handoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback or
                FileTransferTransportHandoffKind.RegularNknRecovery);

    private bool TryRecordPendingFileTransferV6HandoffLocked(
        string? sessionId,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string trigger)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !ShouldPersistPendingFileTransferV6Handoff(handoffKind, targetTransport))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        var intent = new FileTransferV6PendingHandoffIntent(
            normalizedSessionId,
            SanitizeLogToken(reason),
            handoffKind,
            targetTransport,
            DateTimeOffset.UtcNow);
        pendingFileTransferV6HandoffsBySession[normalizedSessionId] = intent;
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_v6_pending_handoff_recorded; session_id={SanitizeLogToken(normalizedSessionId)}; reason={intent.Reason}; trigger={SanitizeLogToken(trigger)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}");
        return true;
    }

    private bool TryRecordPendingFileTransferV6Handoff(
        string? sessionId,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string trigger)
    {
        lock (gate)
        {
            return TryRecordPendingFileTransferV6HandoffLocked(
                sessionId,
                reason,
                handoffKind,
                targetTransport,
                trigger);
        }
    }

    private bool TryReplayPendingFileTransferV6Handoff(TransportFileTransferDataSession session, string trigger)
    {
        FileTransferV6PendingHandoffIntent? intent = null;
        lock (gate)
        {
            if (session.IsDisposed ||
                !fileTransferDataSessions.TryGetValue(session.TransferId, out var current) ||
                !ReferenceEquals(current, session) ||
                !pendingFileTransferV6HandoffsBySession.TryGetValue(session.SessionId, out var candidate))
            {
                return false;
            }

            intent = candidate;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_v6_pending_handoff_replayed; session_id={SanitizeLogToken(session.SessionId)}; transfer_id={SanitizeLogToken(session.TransferId)}; reason={intent.Reason}; trigger={SanitizeLogToken(trigger)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(intent.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(intent.TargetTransport)}");
        if (intent.TargetTransport == FileTransferTransportKind.RegularNkn &&
            intent.HandoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback or
                FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            session.SetAvailability(
                isAvailable: false,
                reason: intent.Reason,
                requiresResumeRequest: true,
                handoffKind: intent.HandoffKind,
                targetTransport: intent.TargetTransport);
        }
        else
        {
            session.RequestHandoff(intent.Reason, intent.HandoffKind, intent.TargetTransport);
        }

        return true;
    }

    private bool TryRequestCurrentTunaActivationHandoffForFileTransferSession(TransportFileTransferDataSession session, string trigger)
    {
        if (session.IsDisposed)
        {
            return false;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(session.TransferId, out var current) ||
                !ReferenceEquals(current, session))
            {
                return false;
            }
        }

        lock (accelerationGate)
        {
            if (accelerationLane?.IsAvailable != true ||
                (accelerationNegotiatedLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                string.IsNullOrWhiteSpace(accelerationSessionId) ||
                string.IsNullOrWhiteSpace(currentSessionId) ||
                !string.Equals(accelerationSessionId, currentSessionId, StringComparison.Ordinal) ||
                !string.Equals(accelerationSessionId, session.SessionId, StringComparison.Ordinal) ||
                string.Equals(accelerationUserStoppedSessionId, currentSessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        const string reason = "active_tuna_session_registered";
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_v6_active_tuna_handoff_synthesized; session_id={SanitizeLogToken(session.SessionId)}; transfer_id={SanitizeLogToken(session.TransferId)}; reason={reason}; trigger={SanitizeLogToken(trigger)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(FileTransferTransportHandoffKind.NormalToTunaActivation)}; target_transport={FormatFileTransferTransportKindForLog(FileTransferTransportKind.Tuna)}");
        session.RequestHandoff(
            reason,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        return true;
    }

    private void ClearPendingFileTransferV6Handoffs(string reason, string? sessionId = null)
    {
        int clearedCount;
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                clearedCount = pendingFileTransferV6HandoffsBySession.Count;
                pendingFileTransferV6HandoffsBySession.Clear();
            }
            else
            {
                clearedCount = pendingFileTransferV6HandoffsBySession.Remove(sessionId.Trim()) ? 1 : 0;
            }
        }

        if (clearedCount > 0)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_pending_handoff_cleared; session_id={SanitizeLogToken(sessionId ?? "(all)")}; reason={SanitizeLogToken(reason)}; cleared_count={clearedCount}");
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
            LogNknInboundEnvelopeDrop(e, "self_source", env: null);
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
            LogNknInboundEnvelopeDrop(e, "parse_failed", env: null);
            Log($"Envelope parse failed (payload_len={e.Payload.Length})");
            return;
        }

        var envelopeParsedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        NknRuntimeDiagnostics.SetLastEnvelopeType(env.Type.ToString());
        LogNknInboundEnvelopeReceived(e, env);

        if (env.Type != MsgType.JoinRequest &&
            !string.IsNullOrWhiteSpace(env.Code) &&
            TryGetCurrentEnvelopeCode(out var expectedEnvelopeCode) &&
            !string.Equals(env.Code, expectedEnvelopeCode, StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.SetLastError("envelope_code_mismatch");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("code_mismatch");
            LogNknInboundEnvelopeDrop(e, "code_mismatch", env);
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
            LogNknInboundEnvelopeDrop(e, "duplicate", env);
            return;
        }

        NknRuntimeDiagnostics.IncrementMessagesReceived();
        Log($"Envelope received (type={env.Type}, payload_len={env.Payload.Length}, msg_id={env.MessageId}, reply_to={env.ReplyTo ?? "-"})");

        try
        {
            envelopeRouter.RouteInboundMessage(
                new NknInboundEnvelopeContext(
                    e.Source,
                    e.Channel,
                    env,
                    e.BridgeIngressObservedUtcMs,
                    envelopeParsedUtcMs,
                    e.BridgeMessageObservedUtcMs,
                    e.BinaryFrameDecodedUtcMs,
                    e.SocketDataEventEmittedUtcMs,
                    e.WsReceiverWriteEnteredUtcMs,
                    e.WsMessageEmittedUtcMs,
                    e.SdkHandleMsgEnteredUtcMs,
                    e.ClientMessageDispatchUtcMs,
                    e.MultiClientMessageDispatchUtcMs));
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
            LogNknInboundEnvelopeDrop(e, $"dispatch_{ex.GetType().Name}", env);
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
            case MsgType.ScreenSharePressureState:
                HandleScreenSharePressureState(source, env);
                break;
            case MsgType.ScreenShareVideoStreamConfig:
                HandleScreenShareVideoStreamConfig(source, env);
                break;
            case MsgType.ScreenShareVideoKeyframeRequest:
                HandleScreenShareVideoKeyframeRequest(source, env);
                break;
            case MsgType.ScreenShareRecoveryReceipt:
                HandleScreenShareRecoveryReceipt(source, env);
                break;
            case MsgType.ScreenShareCursorState:
                HandleScreenShareCursorState(source, env);
                break;
            case MsgType.TransportAccelerationOffer:
                HandleTransportAccelerationOffer(source, env);
                break;
            case MsgType.TransportAccelerationAnswer:
                HandleTransportAccelerationAnswer(source, env);
                break;
            case MsgType.TransportAccelerationAnswerAck:
                HandleTransportAccelerationAnswerAck(source, env);
                break;
            case MsgType.TransportAccelerationDown:
                HandleTransportAccelerationDown(source, env);
                break;
            case MsgType.TransportAccelerationPayerIntent:
                HandleTransportAccelerationPayerIntent(source, env);
                break;
            default:
                throw new InvalidOperationException($"Control channel cannot route {env.Type}.");
        }
    }

    internal void RouteScreenShareEnvelope(NknInboundEnvelopeContext inboundContext)
    {
        switch (inboundContext.Envelope.Type)
        {
            case MsgType.ScreenShareFrame:
                HandleScreenShareFrame(inboundContext);
                break;
            case MsgType.ScreenShareStop:
                HandleScreenShareStop(inboundContext.Source, inboundContext.Envelope);
                break;
            default:
                throw new InvalidOperationException($"Screen share channel cannot route {inboundContext.Envelope.Type}.");
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
        RecordTunaFallbackNknControlReceived(MsgType.FileTransferCancel, message.SessionId, env.Payload.Length);
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
        RecordTunaFallbackNknControlReceived(MsgType.FileTransferError, message.SessionId, env.Payload.Length);
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
        RecordTunaFallbackNknControlReceived(MsgType.FileTransferComplete, message.SessionId, env.Payload.Length);
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
                return TryPrepareFileTransferCancelDispatch(source, channel, env, out work);
            case MsgType.FileTransferError:
                return TryPrepareFileTransferErrorDispatch(source, channel, env, out work);
            case MsgType.FileTransferComplete:
                return TryPrepareFileTransferCompleteDispatch(source, channel, env, out work);
            case MsgType.FileTransferSessionOpen:
                return TryPrepareFileTransferSessionOpenDispatch(source, env, out work);
            case MsgType.FileTransferDataFrame:
                return TryPrepareFileTransferDataFrameDispatch(source, channel, env, out work);
            case MsgType.FileTransferPauseControl:
                return TryPrepareFileTransferPauseControlDispatch(source, channel, env, out work);
            case MsgType.FileTransferHeartbeat:
                return TryPrepareFileTransferHeartbeatDispatch(source, env, out work);
            case MsgType.FileTransferTransportEpoch:
                return TryPrepareFileTransferTransportEpochDispatch(source, env, out work);
            case MsgType.FileTransferTransportProbe:
                return TryPrepareFileTransferTransportProbeDispatch(source, env, out work);
            case MsgType.FileTransferRepairProof:
                return TryPrepareFileTransferRepairProofDispatch(source, env, out work);
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
        return TryPrepareUnsupportedLegacyFileTransferDispatch(MsgType.FileTransferStart, source, env, out work);
    }

    private bool TryPrepareFileTransferChunkDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        return TryPrepareUnsupportedLegacyFileTransferDispatch(MsgType.FileTransferChunk, source, env, out work, channel);
    }

    private bool TryPrepareFileTransferWindowUpdateDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        return TryPrepareUnsupportedLegacyFileTransferDispatch(MsgType.FileTransferWindowUpdate, source, env, out work);
    }

    private bool TryPrepareFileTransferMissingRangeDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        return TryPrepareUnsupportedLegacyFileTransferDispatch(MsgType.FileTransferMissingRange, source, env, out work);
    }

    private bool TryPrepareFileTransferPressureStateDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        return TryPrepareUnsupportedLegacyFileTransferDispatch(MsgType.FileTransferPressureState, source, env, out work);
    }

    private bool TryPrepareUnsupportedLegacyFileTransferDispatch(
        MsgType messageType,
        string source,
        Envelope env,
        out InboundFileTransferDispatchWork work,
        NknBridgeChannel? channel = null)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, messageType, out var securePayload, channel))
        {
            return false;
        }

        var messageTypeName = MapSecureFileTransferMessageType(messageType);
        var transferId = string.IsNullOrWhiteSpace(securePayload.Metadata.RequestId)
            ? null
            : securePayload.Metadata.RequestId.Trim();
        if (string.IsNullOrWhiteSpace(transferId))
        {
            NknRuntimeDiagnostics.SetLastError($"{messageTypeName}_secure_transfer_id_missing");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageTypeName}_secure_transfer_id_missing");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={messageTypeName}; reason=secure_transfer_id_missing; session_id={securePayload.Metadata.SessionId.Value}; source={source}; msg_id={env.MessageId}");
            Log($"FileTransfer secure envelope rejected (type={messageTypeName}, msg_id={env.MessageId}, reason=secure_transfer_id_missing)");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata(messageTypeName, securePayload.Metadata, transferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession(messageTypeName, securePayload.Metadata.SessionId.Value, transferId, env.MessageId, source, channel))
        {
            return false;
        }

        if (!TryValidateUnsupportedLegacyFileTransferState(messageTypeName, messageType, transferId, env.MessageId, source))
        {
            return false;
        }

        NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_legacy_message_ignored");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_legacy_message_ignored; transport=nkn; message_type={messageTypeName}; transfer_id={transferId}; source={source}; msg_id={env.MessageId}; reason=v4_only");
        return false;
    }

    private bool TryPrepareFileTransferCancelDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferCancel, out var securePayload, channel))
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
            !TryValidateFileTransferMessageSession("file_transfer_cancel", message.SessionId, message.TransferId, env.MessageId, source, channel) ||
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
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferCancel, message.SessionId, env.Payload.Length);
                Log($"FileTransferCancel received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
                FileTransferCancelReceived?.Invoke(this, new FileTransferCancelReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferErrorDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferError, out var securePayload, channel))
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
            !TryValidateFileTransferMessageSession("file_transfer_error", message.SessionId, message.TransferId, env.MessageId, source, channel) ||
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
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferError, message.SessionId, env.Payload.Length);
                Log($"FileTransferError received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, error_code={message.ErrorCode})");
                FileTransferErrorReceived?.Invoke(this, new FileTransferErrorReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferCompleteDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferComplete, out var securePayload, channel))
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
            !TryValidateFileTransferMessageSession("file_transfer_complete", message.SessionId, message.TransferId, env.MessageId, source, channel) ||
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
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferComplete, message.SessionId, env.Payload.Length);
                Log($"FileTransferComplete received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, size_bytes={message.FileSizeBytes})");
                FileTransferCompleteReceived?.Invoke(this, new FileTransferCompleteReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferPauseControlDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferPauseControl, out var securePayload, channel))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializePauseControl(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_pause_control_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_pause_control_payload_invalid");
            Log($"FileTransferPauseControl payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_pause_control", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_pause_control", message.SessionId, message.TransferId, env.MessageId, source, channel) ||
            !TryValidateFileTransferDispatchState("file_transfer_pause_control", MsgType.FileTransferPauseControl, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferPauseControl,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferPauseControl, message.TransferId, source);
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferPauseControl, message.SessionId, env.Payload.Length);
                FileTransferPauseControlReceived?.Invoke(this, new FileTransferPauseControlReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferHeartbeatDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferHeartbeat, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeHeartbeat(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_heartbeat_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_heartbeat_payload_invalid");
            Log($"FileTransferHeartbeat payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_heartbeat", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_heartbeat", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_heartbeat", MsgType.FileTransferHeartbeat, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferHeartbeat,
            message.TransferId,
            () =>
            {
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferHeartbeat, message.SessionId, env.Payload.Length);
                FileTransferHeartbeatReceived?.Invoke(this, new FileTransferHeartbeatReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferTransportEpochDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferTransportEpoch, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeTransportEpoch(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_transport_epoch_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_transport_epoch_payload_invalid");
            Log($"FileTransferTransportEpoch payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_transport_epoch", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_transport_epoch", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_transport_epoch", MsgType.FileTransferTransportEpoch, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferTransportEpoch,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferTransportEpoch, message.TransferId, source);
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferTransportEpoch, message.SessionId, env.Payload.Length);
                FileTransferTransportEpochReceived?.Invoke(this, new FileTransferTransportEpochReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferTransportProbeDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferTransportProbe, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeTransportProbe(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_transport_probe_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_transport_probe_payload_invalid");
            Log($"FileTransferTransportProbe payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_transport_probe", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_transport_probe", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_transport_probe", MsgType.FileTransferTransportProbe, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferTransportProbe,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferTransportProbe, message.TransferId, source);
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferTransportProbe, message.SessionId, env.Payload.Length);
                FileTransferTransportProbeReceived?.Invoke(this, new FileTransferTransportProbeReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferRepairProofDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferRepairProof, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeRepairProof(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_repair_proof_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_repair_proof_payload_invalid");
            Log($"FileTransferRepairProof payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_repair_proof", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_repair_proof", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_repair_proof", MsgType.FileTransferRepairProof, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferRepairProof,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferRepairProof, message.TransferId, source);
                RecordTunaFallbackNknControlReceived(MsgType.FileTransferRepairProof, message.SessionId, env.Payload.Length);
                FileTransferRepairProofReceived?.Invoke(this, new FileTransferRepairProofReceivedEventArgs(message, source));
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

        if (!TryValidateFileTransferDispatchState(
                "file_transfer_session_open",
                MsgType.FileTransferSessionOpen,
                message.TransferId,
                env.MessageId,
                source))
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
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferDataFrame, out var securePayload, channel))
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

        if (!FileTransferProtocol.IsV6DataFrame(frame))
        {
            NknRuntimeDiagnostics.SetLastError("file_transfer_data_frame_protocol_not_v6");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("file_transfer_data_frame_protocol_not_v6");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=protocol_not_v6; session_id={frame.SessionId}; transfer_id={frame.TransferId}; source={source ?? "(none)"}; msg_id={env.MessageId}; frame_type={frame.Type}");
            Log($"FileTransfer message rejected (type=file_transfer_data_frame, msg_id={env.MessageId}, reason=protocol_not_v6, transfer_id={frame.TransferId})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_data_frame", securePayload.Metadata, frame.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_data_frame", frame.SessionId, frame.TransferId, env.MessageId, source, channel))
        {
            return false;
        }

        if (!TryValidateAndTrackFileTransferDataFrame(frame, inbound: true, applyStateChange: true, out var failureReason))
        {
            if (ShouldEchoCancelForLateFileTransferDataFrame(frame, failureReason))
            {
                ScheduleFileTransferCancelEcho(frame, source, channel, failureReason);
            }

            if (IsBenignLateFileTransferDataFrameRejection(frame, failureReason))
            {
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason={failureReason}; source={source ?? "(none)"}; msg_id={env.MessageId}");
                Log($"FileTransfer data frame ignored (frame_type={frame.Type}, msg_id={env.MessageId}, reason={failureReason}, transfer_id={frame.TransferId})");
                return false;
            }

            NknRuntimeDiagnostics.SetLastError($"file_transfer_data_frame_{failureReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"file_transfer_data_frame_{failureReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason={failureReason}; session_id={frame.SessionId}; transfer_id={frame.TransferId}; source={source ?? "(none)"}; msg_id={env.MessageId}");
            Log($"FileTransfer message rejected (type=file_transfer_data_frame, msg_id={env.MessageId}, reason={failureReason}, transfer_id={frame.TransferId})");
            return false;
        }

        if (frame is FileTransferCancelFrameV4 cancelFrame)
        {
            work = CreateInboundFileTransferDispatchWork(
                MsgType.FileTransferCancel,
                frame.TransferId,
                () =>
                {
                    LocalOperationalLog.Info(
                        "SessionSecurity",
                        $"event=filetransfer_v4_cancel_frame_received; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; source={source}; msg_id={env.MessageId}; lane={MapBridgeChannel(channel)}");
                    LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDataFrame, frame.TransferId, source);
                    FileTransferCancelReceived?.Invoke(
                        this,
                        new FileTransferCancelReceivedEventArgs(
                            new FileTransferCancelV1
                            {
                                SessionId = cancelFrame.SessionId,
                                TransferId = cancelFrame.TransferId,
                                Reason = cancelFrame.Reason,
                            },
                            source));
                });
            return true;
        }

        if (frame is FileTransferErrorFrameV4 errorFrame)
        {
            work = CreateInboundFileTransferDispatchWork(
                MsgType.FileTransferError,
                frame.TransferId,
                () =>
                {
                    LocalOperationalLog.Info(
                        "SessionSecurity",
                        $"event=filetransfer_v4_error_frame_received; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; source={source}; msg_id={env.MessageId}; lane={MapBridgeChannel(channel)}; error_code={errorFrame.ErrorCode ?? "(none)"}");
                    LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDataFrame, frame.TransferId, source);
                    FileTransferErrorReceived?.Invoke(
                        this,
                        new FileTransferErrorReceivedEventArgs(
                            new FileTransferErrorV1
                            {
                                SessionId = errorFrame.SessionId,
                                TransferId = errorFrame.TransferId,
                                ErrorCode = errorFrame.ErrorCode ?? "remote_error",
                                Message = errorFrame.Message,
                            },
                            source));
                });
            return true;
        }

        var receivedTransportKind = ResolveInboundFileTransferDataFrameTransportKind(channel);
        var effectiveTransport = FormatFileTransferTransportKindForLog(receivedTransportKind);
        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferDataFrame,
            frame.TransferId,
            () =>
            {
                if (receivedTransportKind != FileTransferTransportKind.Tuna)
                {
                    RecordTunaFallbackFileTransferDataFrameReceived(
                        frame,
                        channel,
                        env.Payload.Length,
                        frame.SessionId);
                }

                DeliverFileTransferDataFrame(frame, channel, receivedTransportKind);
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDataFrame, frame.TransferId, source, effectiveTransport);
            });
        return true;
    }

    private static FileTransferTransportKind ResolveInboundFileTransferDataFrameTransportKind(NknBridgeChannel channel)
    {
        if (handlingTunaAcceleratedInboundMessage &&
            channel == NknBridgeChannel.Bulk)
        {
            return FileTransferTransportKind.Tuna;
        }

        return channel switch
        {
            NknBridgeChannel.Bulk => FileTransferTransportKind.RegularNkn,
            NknBridgeChannel.Control => FileTransferTransportKind.RegularNkn,
            _ => FileTransferTransportKind.Unknown,
        };
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
            or MsgType.FileTransferComplete
            or MsgType.FileTransferPauseControl
            or MsgType.FileTransferTransportEpoch
            or MsgType.FileTransferTransportProbe
            or MsgType.FileTransferRepairProof;
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

    private bool TryValidateUnsupportedLegacyFileTransferState(
        string messageType,
        MsgType transportMessageType,
        string transferId,
        string messageId,
        string? source)
    {
        if (TryValidateAndTrackFileTransferMessage(transportMessageType, transferId, inbound: true, applyStateChange: false, out var failureReason))
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; transfer_id={transferId}; source={source ?? "(none)"}; msg_id={messageId}");
        Log($"FileTransfer legacy message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={transferId})");
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
        => (failureReason is "unknown_transfer_id" or "transfer_already_terminal") &&
           transportMessageType is MsgType.FileTransferWindowUpdate
               or MsgType.FileTransferMissingRange
               or MsgType.FileTransferPressureState
               or MsgType.FileTransferPauseControl
               or MsgType.FileTransferHeartbeat
               or MsgType.FileTransferCancel
               or MsgType.FileTransferError
               or MsgType.FileTransferComplete
               or MsgType.FileTransferTransportEpoch
               or MsgType.FileTransferTransportProbe
               or MsgType.FileTransferRepairProof;

    private static bool IsBenignLateFileTransferDataFrameRejection(FileTransferDataFrame frame, string failureReason)
        => ((failureReason is "unknown_transfer_id" or "transfer_already_terminal") &&
            (IsReceiverFeedbackDataFrame(frame) || IsV5RecoveryControlDataFrame(frame) || IsTerminalDataFrame(frame) || frame is FileTransferPauseControlFrameV4)) ||
           failureReason == "post_terminal_late_frame_canceled" ||
           (failureReason == "post_completion_late_sender_frame" && IsSenderDataFrame(frame)) ||
           (failureReason == "transfer_already_terminal" && IsSenderDataFrame(frame));

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

    private void HandleScreenSharePressureState(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ScreenSharePressureState, out var securePayload))
        {
            return;
        }

        if (!ScreenSharePressureStateCodec.TryDeserialize(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_pressure_state_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_pressure_state_payload_invalid");
            Log($"ScreenSharePressureState payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("screenshare_pressure_state", securePayload.Metadata, requestId: null, env.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_pressure_state",
                message.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("pressure_state", message.SessionId))
        {
            return;
        }

        ScreenSharePressureStateReceived?.Invoke(this, new ScreenSharePressureStateReceivedEventArgs(message, source));
    }

    private void HandleScreenShareVideoStreamConfig(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ScreenShareVideoStreamConfig, out var securePayload))
        {
            return;
        }

        if (!ScreenShareVideoPayloadCodec.TryDeserializeStreamConfig(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_stream_config_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_video_stream_config_payload_invalid");
            Log($"ScreenShareVideoStreamConfig payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("screenshare_video_stream_config", securePayload.Metadata, requestId: null, env.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_video_stream_config",
                message.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("stream_config", message.SessionId))
        {
            return;
        }

        secureScreenShareFrameReassembler.OnStreamConfig(message);
        NknRuntimeDiagnostics.SetMediaPlaneGeneration(Math.Max(0, message.StreamEpoch));
        NknRuntimeDiagnostics.SetMediaPlaneAttached(true);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_video_stream_config_received; session_id={message.SessionId}; stream_epoch={Math.Max(0, message.StreamEpoch)}; source={source}; config_bytes={message.DecoderConfigData?.Length ?? 0}; encoding={message.Encoding}; codec_profile={message.CodecProfile}");
        ScreenShareVideoStreamConfigReceived?.Invoke(this, new ScreenShareVideoStreamConfigReceivedEventArgs(message, source));
    }

    private void HandleScreenShareVideoKeyframeRequest(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ScreenShareVideoKeyframeRequest, out var securePayload))
        {
            return;
        }

        if (!ScreenShareVideoKeyframeRequestCodec.TryDeserialize(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_keyframe_request_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_video_keyframe_request_payload_invalid");
            Log($"ScreenShareVideoKeyframeRequest payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("screenshare_video_keyframe_request", securePayload.Metadata, requestId: null, env.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_video_keyframe_request",
                message.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("keyframe_request", message.SessionId))
        {
            return;
        }

        ScreenShareVideoKeyframeRequestReceived?.Invoke(this, new ScreenShareVideoKeyframeRequestReceivedEventArgs(message, source));
    }

    private void HandleScreenShareRecoveryReceipt(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ScreenShareRecoveryReceipt, out var securePayload))
        {
            return;
        }

        if (!ScreenShareRecoveryReceiptCodec.TryDeserialize(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_recovery_receipt_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_recovery_receipt_payload_invalid");
            Log($"ScreenShareRecoveryReceipt payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("screenshare_recovery_receipt", securePayload.Metadata, requestId: null, env.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_recovery_receipt",
                message.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("recovery_receipt", message.SessionId))
        {
            return;
        }

        ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, source));
    }

    private void HandleScreenShareCursorState(string source, Envelope env)
    {
        if (!SessionSupportsScreenShareCursorOverlay)
        {
            return;
        }

        if (!TryDecryptControlPayload(source, env, MsgType.ScreenShareCursorState, out var securePayload))
        {
            return;
        }

        if (!ScreenShareCursorStateCodec.TryDeserialize(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_cursor_state_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_cursor_state_payload_invalid");
            Log($"ScreenShareCursorState payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("screenshare_cursor_state", securePayload.Metadata, requestId: null, env.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_cursor_state",
                message.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("cursor_state", message.SessionId))
        {
            return;
        }

        ScreenShareCursorStateReceived?.Invoke(this, new ScreenShareCursorStateReceivedEventArgs(message, source));
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
        var expectedSource = string.Equals(messageType, "screenshare_frame", StringComparison.Ordinal)
            ? ResolveExpectedRemoteScreenShareFrameSourcesForLog()
            : ResolveExpectedRemotePeerAddressForCurrentSession();
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
        else if (string.Equals(messageType, "screenshare_frame", StringComparison.Ordinal))
        {
            if (!MatchesExpectedRemoteScreenShareFrameSource(normalizedSource))
            {
                failureReason = "source_identity_mismatch";
            }
            else
            {
                return true;
            }
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
        if (string.Equals(messageType, "screenshare_frame", StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.IncrementMediaPlaneSessionMismatchRejectCount();
            NknRuntimeDiagnostics.SetLastMediaPlaneRejectReason(failureReason);
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screen_share_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}; request_id={requestId ?? "(none)"}; msg_id={messageId}");
        Log($"ScreenShare message rejected (type={messageType}, msg_id={messageId}, request_id={requestId ?? "(none)"}, reason={failureReason})");
        return false;
    }

    private bool MatchesExpectedRemoteScreenShareFrameSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var normalizedSource = source.Trim();

        var expectedMediaSource = ResolveExpectedRemoteMediaPeerAddressForCurrentSession();
        if (!string.IsNullOrWhiteSpace(expectedMediaSource))
        {
            var normalizedExpectedMediaSource = expectedMediaSource.Trim();
            if (string.Equals(normalizedSource, normalizedExpectedMediaSource, StringComparison.Ordinal) ||
                AddressMatchesForSessionPolicy(normalizedSource, normalizedExpectedMediaSource))
            {
                return true;
            }
        }

        var expectedControlSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedControlSource))
        {
            return false;
        }

        var normalizedExpectedControlSource = expectedControlSource.Trim();
        return string.Equals(normalizedSource, normalizedExpectedControlSource, StringComparison.Ordinal) ||
               AddressMatchesForSessionPolicy(normalizedSource, normalizedExpectedControlSource);
    }

    private string? ResolveExpectedRemoteScreenShareFrameSourcesForLog()
    {
        var expectedMediaSource = ResolveExpectedRemoteMediaPeerAddressForCurrentSession();
        var expectedControlSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedMediaSource))
        {
            return expectedControlSource;
        }

        if (string.IsNullOrWhiteSpace(expectedControlSource) ||
            string.Equals(expectedMediaSource, expectedControlSource, StringComparison.Ordinal))
        {
            return expectedMediaSource;
        }

        return $"{expectedMediaSource}|{expectedControlSource}";
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

    private static bool IsExpectedControlReplayDuplicate(MsgType messageType)
        => messageType is MsgType.ScreenSharePressureState or
            MsgType.ScreenShareCursorState or
            MsgType.ControlDisplayInfo or
            MsgType.TransportAccelerationOffer or
            MsgType.TransportAccelerationAnswer or
            MsgType.TransportAccelerationAnswerAck or
            MsgType.TransportAccelerationDown or
            MsgType.TransportAccelerationPayerIntent;

    private bool TrySuppressExpectedControlReplayDuplicate(
        MsgType messageType,
        string mappedMessageType,
        string sessionId,
        string? source,
        long sequence,
        string messageId)
    {
        if (!IsExpectedControlReplayDuplicate(messageType))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "(none)" : source.Trim();
        var key = $"{mappedMessageType}|{sessionId}|{normalizedSource}";
        long suppressedCount = 0;
        long sampleWindowMs = 0;
        long lastSequence = sequence;
        string lastMessageId = messageId;
        string lastSource = normalizedSource;
        var shouldLogSummary = false;

        lock (expectedControlReplayDuplicateLogGate)
        {
            if (!expectedControlReplayDuplicateSuppressions.TryGetValue(key, out var state))
            {
                state = new ExpectedControlReplayDuplicateSuppressionState
                {
                    WindowStartedUtc = now,
                };
                expectedControlReplayDuplicateSuppressions[key] = state;
            }

            state.SuppressedCount++;
            state.LastSequence = sequence;
            state.LastMessageId = messageId;
            state.LastSource = normalizedSource;

            var elapsed = now - state.WindowStartedUtc;
            if (elapsed >= ExpectedControlReplayDuplicateSummaryWindow)
            {
                shouldLogSummary = true;
                suppressedCount = state.SuppressedCount;
                sampleWindowMs = Math.Max(0, (long)elapsed.TotalMilliseconds);
                lastSequence = state.LastSequence;
                lastMessageId = state.LastMessageId;
                lastSource = state.LastSource;
                state.SuppressedCount = 0;
                state.WindowStartedUtc = now;
            }
        }

        if (shouldLogSummary)
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=control_duplicate_replay_suppressed; message_type={mappedMessageType}; reason=replay_duplicate; session_id={sessionId}; source={lastSource}; suppressed_count={suppressedCount}; sample_window_ms={sampleWindowMs}; last_sequence={lastSequence}; last_msg_id={lastMessageId}");
        }

        return true;
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

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
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
            var mappedMessageType = MapSecureControlMessageType(messageType);
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            if (replay == SessionReplaySequenceResult.Duplicate &&
                TrySuppressExpectedControlReplayDuplicate(
                    messageType,
                    mappedMessageType,
                    sessionId.Value,
                    source,
                    securePayload.Metadata.Sequence,
                    env.MessageId))
            {
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{mappedMessageType}_duplicate_suppressed");
                return false;
            }

            NknRuntimeDiagnostics.SetLastError($"{mappedMessageType}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{mappedMessageType}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={mappedMessageType}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
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

    private byte[] CreateSecureFileTransferPayload(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        bool useBulkIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = useBulkIdentity || messageType == MsgType.FileTransferChunk
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

    private byte[] CreateSecureFileTransferPayloadForBudgetEstimate(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        bool useBulkIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = useBulkIdentity || messageType == MsgType.FileTransferChunk
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
        var replayWindowSize = 0;
        var replayHighestSequence = 0L;
        var replayLowestSequence = 0L;
        var replaySequenceDeltaFromHighest = 0L;
        lock (controlSecureStateGate)
        {
            replay = inboundScreenShareReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
            replayWindowSize = inboundScreenShareReplayWindow.WindowSize;
            if (inboundScreenShareReplayWindow.HasHighestSequence)
            {
                replayHighestSequence = inboundScreenShareReplayWindow.HighestAcceptedSequence;
                replayLowestSequence = inboundScreenShareReplayWindow.LowestAcceptedSequence;
                replaySequenceDeltaFromHighest = securePayload.Metadata.Sequence - replayHighestSequence;
            }
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
            NknRuntimeDiagnostics.SetLastMediaPlaneRejectReason(replayReason);
            NknRuntimeDiagnostics.IncrementMediaPlaneReplayRejectCount();
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={MapSecureScreenShareMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}; window_size={replayWindowSize}; highest_sequence={replayHighestSequence}; lowest_sequence={replayLowestSequence}; sequence_delta_from_highest={replaySequenceDeltaFromHighest}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence}, window={replayWindowSize}, highest={replayHighestSequence}, lowest={replayLowestSequence}, delta={replaySequenceDeltaFromHighest})");
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
        out SessionSecureEnvelopePayload securePayload,
        NknBridgeChannel? channel = null)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemoteFileTransferSenderForMessage(messageType, source, channel);
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
        string? source,
        NknBridgeChannel? channel = null)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = ResolveExpectedRemoteFileTransferSourceForMessage(messageType, source, channel);
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

    private string? ResolveExpectedRemoteFileTransferSenderForMessage(
        MsgType messageType,
        string? source,
        NknBridgeChannel? channel)
    {
        if (messageType == MsgType.FileTransferChunk)
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (messageType == MsgType.FileTransferDataFrame &&
            channel == NknBridgeChannel.Bulk)
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (messageType == MsgType.FileTransferDataFrame &&
            SourceMatchesExpectedRemoteBulkPeer(source))
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (IsBulkDuplicatedFileTransferLifecycleMessage(messageType) &&
            channel == NknBridgeChannel.Bulk)
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (IsBulkDuplicatedFileTransferLifecycleMessage(messageType) &&
            SourceMatchesExpectedRemoteBulkPeer(source))
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        return ResolveExpectedRemotePeerAddressForCurrentSession();
    }

    private string? ResolveExpectedRemoteFileTransferSourceForMessage(
        string messageType,
        string? source,
        NknBridgeChannel? channel)
    {
        if (string.Equals(messageType, "file_transfer_chunk", StringComparison.Ordinal))
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (string.Equals(messageType, "file_transfer_data_frame", StringComparison.Ordinal) &&
            channel == NknBridgeChannel.Bulk)
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (string.Equals(messageType, "file_transfer_data_frame", StringComparison.Ordinal) &&
            SourceMatchesExpectedRemoteBulkPeer(source))
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (IsBulkDuplicatedFileTransferLifecycleMessage(messageType) &&
            channel == NknBridgeChannel.Bulk)
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        if (IsBulkDuplicatedFileTransferLifecycleMessage(messageType) &&
            SourceMatchesExpectedRemoteBulkPeer(source))
        {
            return ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        }

        return ResolveExpectedRemotePeerAddressForCurrentSession();
    }

    private static bool IsBulkDuplicatedFileTransferLifecycleMessage(string messageType)
        => string.Equals(messageType, "file_transfer_cancel", StringComparison.Ordinal) ||
           string.Equals(messageType, "file_transfer_error", StringComparison.Ordinal) ||
           string.Equals(messageType, "file_transfer_complete", StringComparison.Ordinal) ||
           string.Equals(messageType, "file_transfer_pause_control", StringComparison.Ordinal);

    private static bool IsBulkDuplicatedFileTransferLifecycleMessage(MsgType messageType)
        => messageType is MsgType.FileTransferCancel or
            MsgType.FileTransferError or
            MsgType.FileTransferComplete or
            MsgType.FileTransferPauseControl;

    private bool SourceMatchesExpectedRemoteBulkPeer(string? source)
    {
        var expectedBulkSource = ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var normalizedExpectedBulkSource = string.IsNullOrWhiteSpace(expectedBulkSource) ? null : expectedBulkSource.Trim();
        return !string.IsNullOrWhiteSpace(normalizedSource) &&
               !string.IsNullOrWhiteSpace(normalizedExpectedBulkSource) &&
               string.Equals(normalizedSource, normalizedExpectedBulkSource, StringComparison.Ordinal);
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
            ClearActiveFileTransferRuntimeTrackingLocked("control_session_key_reset");
            fileTransferStates.Clear();
            fileTransferTerminalTombstones.Clear();
        }

        ClearFileTransferDataSessionRemoteOpenSuppressed();
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
            ClearActiveFileTransferRuntimeTrackingLocked("control_secure_state_reset");
            fileTransferStates.Clear();
            fileTransferTerminalTombstones.Clear();
        }

        ClearFileTransferDataSessionRemoteOpenSuppressed();
        ResetInboundFileTransferDispatchQueue();
    }

    private void ClearFileTransferDataSessionRemoteOpenSuppressed()
    {
        lock (gate)
        {
            fileTransferDataSessionRemoteOpenSuppressed.Clear();
        }
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

    private bool TryValidateAndTrackFileTransferDataFrame(
        FileTransferDataFrame frame,
        bool inbound,
        bool applyStateChange,
        out string failureReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(frame.TransferId);

        lock (controlSecureStateGate)
        {
            if (!TryGetNextFileTransferDataFrameState(frame, normalizedTransferId, inbound, out var nextState, out failureReason))
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

    private void TrackOutboundFileTransferDataFrameLifecycle(FileTransferDataFrame frame)
    {
        if (!TryValidateAndTrackFileTransferDataFrame(frame, inbound: false, applyStateChange: true, out var failureReason))
        {
            var level = IsBenignLateFileTransferDataFrameRejection(frame, failureReason) ? "Info" : "Warn";
            var message =
                $"event=filetransfer_data_frame_state_race; transport=nkn; frame_type={frame.Type}; reason={failureReason}; transfer_id={frame.TransferId}; session_id={frame.SessionId}; source={LocalPeerAddress}";
            if (string.Equals(level, "Info", StringComparison.Ordinal))
            {
                LocalOperationalLog.Info("SessionSecurity", message);
            }
            else
            {
                LocalOperationalLog.Warn("SessionSecurity", message);
            }
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
            if (messageType == MsgType.FileTransferCancel &&
                IsRecentTerminalFileTransferLocked(transferId, out var terminalPhase) &&
                terminalPhase == FileTransferTransportPhase.Canceled)
            {
                NknRuntimeDiagnostics.IncrementFileTransferDuplicateCancelAcked();
                nextState = new FileTransferTransportState(InitiatedLocally: !inbound, FileTransferTransportPhase.Canceled);
                return true;
            }

            failureReason = "unknown_transfer_id";
            return false;
        }

        if (currentState.IsTerminal)
        {
            if (messageType == MsgType.FileTransferCancel &&
                currentState.Phase == FileTransferTransportPhase.Canceled)
            {
                NknRuntimeDiagnostics.IncrementFileTransferDuplicateCancelAcked();
                nextState = currentState;
                return true;
            }

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

        if (messageType == MsgType.FileTransferSessionOpen)
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "session_open_requires_accept";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_session_open_for_local_receiver" : "unexpected_outbound_session_open_for_local_receiver";
                return false;
            }

            nextState = currentState.Phase == FileTransferTransportPhase.Accepted
                ? currentState with { Phase = FileTransferTransportPhase.Started }
                : currentState;
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

        if (messageType is MsgType.FileTransferPauseControl
            or MsgType.FileTransferHeartbeat
            or MsgType.FileTransferTransportEpoch
            or MsgType.FileTransferTransportProbe
            or MsgType.FileTransferRepairProof)
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = $"{MapSecureFileTransferMessageType(messageType)}_requires_accept";
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

    private bool TryGetNextFileTransferDataFrameState(
        FileTransferDataFrame frame,
        string transferId,
        bool inbound,
        out FileTransferTransportState nextState,
        out string failureReason)
    {
        var hasExisting = fileTransferStates.TryGetValue(transferId, out var currentState);
        nextState = default;
        failureReason = string.Empty;

        if (!hasExisting)
        {
            var hasRecentTerminal = IsRecentTerminalFileTransferLocked(transferId, out var terminalPhase);
            failureReason = FileTransferV4TransportStateClassifier.ClassifyMissingTransferDataFrame(
                isSenderDataFrame: IsSenderDataFrame(frame),
                hasRecentTerminal,
                terminalPhase);
            return false;
        }

        if (currentState.IsTerminal)
        {
            failureReason = "transfer_already_terminal";
            return false;
        }

        if (frame is FileTransferPauseControlFrameV4
            or FileTransferCompleteFrameV4
            or FileTransferCancelFrameV4
            or FileTransferErrorFrameV4)
        {
            failureReason = "lifecycle_data_frame_unsupported";
            return false;
        }

        if (IsV5RecoveryControlDataFrame(frame))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "recovery_control_requires_start";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (IsSenderStateControlDataFrame(frame, currentState, inbound))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "state_control_requires_start";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (IsReceiverFeedbackDataFrame(frame, currentState, inbound))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "feedback_requires_start";
                return false;
            }

            nextState = currentState;
            return true;
        }

        if (frame is FileTransferStateFrameV4)
        {
            failureReason = inbound
                ? "unexpected_inbound_state_frame_direction"
                : "unexpected_outbound_state_frame_direction";
            return false;
        }

        if (IsSenderDataFrame(frame))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "data_frame_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_data_frame_for_local_sender" : "unexpected_outbound_data_frame_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        failureReason = "unsupported_data_frame_type";
        return false;
    }

    private bool ShouldEchoCancelForLateFileTransferDataFrame(FileTransferDataFrame frame, string failureReason)
    {
        if (frame is FileTransferCancelFrameV4)
        {
            return false;
        }

        if (failureReason == "post_terminal_late_frame_canceled")
        {
            return true;
        }

        return failureReason == "transfer_already_terminal" &&
               IsRecentTerminalFileTransferLocked(frame.TransferId, out var terminalPhase) &&
               terminalPhase == FileTransferTransportPhase.Canceled;
    }

    private void ScheduleFileTransferCancelEcho(
        FileTransferDataFrame frame,
        string? source,
        NknBridgeChannel channel,
        string triggerReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(frame.TransferId);
        var normalizedSessionId = string.IsNullOrWhiteSpace(frame.SessionId) ? CurrentSessionSecurityState.SessionId?.Value : frame.SessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (controlSecureStateGate)
        {
            if (fileTransferCancelEchoLastSent.TryGetValue(normalizedTransferId, out var lastSent) &&
                now - lastSent < FileTransferCancelEchoMinInterval)
            {
                return;
            }

            fileTransferCancelEchoLastSent[normalizedTransferId] = now;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(AckWaitTimeout);
                    await SendFileTransferCancelAsync(
                            new FileTransferCancelV1
                            {
                                SessionId = normalizedSessionId,
                                TransferId = normalizedTransferId,
                                Reason = FileTransferResultCodes.CanceledLocal,
                            },
                            timeout.Token)
                        .ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "SessionSecurity",
                        $"event=filetransfer_cancel_echo_sent; transport=nkn; transfer_id={normalizedTransferId}; session_id={normalizedSessionId}; trigger_reason={triggerReason}; late_frame_type={frame.Type}; source={source ?? "(none)"}; lane={MapBridgeChannel(channel)}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "SessionSecurity",
                        $"event=filetransfer_cancel_echo_failed; transport=nkn; transfer_id={normalizedTransferId}; session_id={normalizedSessionId}; trigger_reason={triggerReason}; late_frame_type={frame.Type}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private static bool IsSenderDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferManifestFrameV4
            or FileTransferChunkBatchFrameV4;

    private static bool IsSenderStateControlDataFrame(
        FileTransferDataFrame frame,
        FileTransferTransportState currentState,
        bool inbound)
        => frame is FileTransferStateFrameV4 state &&
           currentState.InitiatedLocally == !inbound &&
           (state.TransferPaused || !string.IsNullOrWhiteSpace(state.TransferPauseReason));

    private static bool IsReceiverFeedbackDataFrame(
        FileTransferDataFrame frame,
        FileTransferTransportState currentState,
        bool inbound)
        => frame is FileTransferStateFrameV4 &&
           currentState.InitiatedLocally == inbound;

    private static bool IsReceiverFeedbackDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferStateFrameV4;

    private static bool IsV5RecoveryControlDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferTransportEpochFrameV6
            or FileTransferTransportProbeFrameV6
            or FileTransferFrontierRequestFrameV6
            or FileTransferRepairProofFrameV6;

    private static bool IsTerminalDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferCompleteFrameV4
            or FileTransferCancelFrameV4
            or FileTransferErrorFrameV4;

    private static string MapFileTransferTerminalPhase(FileTransferTransportPhase phase)
        => phase switch
        {
            FileTransferTransportPhase.Completed => "completed",
            FileTransferTransportPhase.Declined => "declined",
            FileTransferTransportPhase.Canceled => "canceled",
            FileTransferTransportPhase.Failed => "failed",
            _ => "unknown",
        };

    private static class FileTransferV4TransportStateClassifier
    {
        public static string ClassifyMissingTransferDataFrame(
            bool isSenderDataFrame,
            bool hasRecentTerminal,
            FileTransferTransportPhase terminalPhase)
        {
            if (hasRecentTerminal &&
                terminalPhase == FileTransferTransportPhase.Canceled)
            {
                return "post_terminal_late_frame_canceled";
            }

            if (!isSenderDataFrame || !hasRecentTerminal)
            {
                return "unknown_transfer_id";
            }

            return terminalPhase == FileTransferTransportPhase.Completed
                ? "post_completion_late_sender_frame"
                : $"post_terminal_late_sender_frame_{MapFileTransferTerminalPhase(terminalPhase)}";
        }
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
            PruneExpiredFileTransferTerminalTombstonesLocked();
            fileTransferTerminalTombstones[transferId] = new FileTransferTerminalTombstone(
                nextState.Phase,
                DateTimeOffset.UtcNow.UtcTicks);
            fileTransferStates.Remove(transferId);
            UnregisterActiveFileTransferRuntime(transferId);
            return;
        }

        fileTransferTerminalTombstones.Remove(transferId);
        fileTransferStates[transferId] = nextState;
        if (nextState.Phase is not FileTransferTransportPhase.Offered)
        {
            RegisterActiveFileTransferRuntime(transferId);
        }
    }

    private bool IsRecentTerminalFileTransferLocked(string transferId, out FileTransferTransportPhase terminalPhase)
    {
        PruneExpiredFileTransferTerminalTombstonesLocked();

        if (fileTransferTerminalTombstones.TryGetValue(transferId, out var tombstone))
        {
            terminalPhase = tombstone.Phase;
            return true;
        }

        terminalPhase = default;
        return false;
    }

    private void PruneExpiredFileTransferTerminalTombstonesLocked()
    {
        if (fileTransferTerminalTombstones.Count == 0)
        {
            return;
        }

        var cutoffTicks = DateTimeOffset.UtcNow.UtcTicks - FileTransferTerminalTombstoneRetention.Ticks;
        List<string>? expiredTransferIds = null;
        foreach (var entry in fileTransferTerminalTombstones)
        {
            if (entry.Value.CompletedUtcTicks > cutoffTicks)
            {
                continue;
            }

            expiredTransferIds ??= new List<string>();
            expiredTransferIds.Add(entry.Key);
        }

        if (expiredTransferIds is null)
        {
            return;
        }

        foreach (var expiredTransferId in expiredTransferIds)
        {
            fileTransferTerminalTombstones.Remove(expiredTransferId);
        }
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
            MsgType.ScreenSharePressureState => "screenshare_pressure_state",
            MsgType.ScreenShareVideoStreamConfig => "screenshare_video_stream_config",
            MsgType.ScreenShareVideoKeyframeRequest => "screenshare_video_keyframe_request",
            MsgType.ScreenShareRecoveryReceipt => "screenshare_recovery_receipt",
            MsgType.ScreenShareCursorState => "screenshare_cursor_state",
            MsgType.TransportAccelerationOffer => "transport_acceleration_offer",
            MsgType.TransportAccelerationAnswer => "transport_acceleration_answer",
            MsgType.TransportAccelerationAnswerAck => "transport_acceleration_answer_ack",
            MsgType.TransportAccelerationDown => "transport_acceleration_down",
            MsgType.TransportAccelerationPayerIntent => "transport_acceleration_payer_intent",
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
            MsgType.FileTransferPauseControl => "file_transfer_pause_control",
            MsgType.FileTransferHeartbeat => "file_transfer_heartbeat",
            MsgType.FileTransferTransportEpoch => "file_transfer_transport_epoch",
            MsgType.FileTransferTransportProbe => "file_transfer_transport_probe",
            MsgType.FileTransferRepairProof => "file_transfer_repair_proof",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure file-transfer message type."),
        };
    }

    private static string MapSecureChatMessageType() => "chat_message";

    private void DeliverFileTransferDataFrame(
        FileTransferDataFrame frame,
        NknBridgeChannel channel,
        FileTransferTransportKind receivedTransportKind)
    {
        TransportFileTransferDataSession? session;
        lock (gate)
        {
            fileTransferDataSessions.TryGetValue(frame.TransferId, out var existingSession);
            session = existingSession;
            if (session is not null &&
                session.IsDisposed)
            {
                fileTransferDataSessions.Remove(frame.TransferId);
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_session_recreated; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason=disposed_existing_session_on_deliver");
                session = null;
            }

            if (session is null)
            {
                if (fileTransferDataSessionRemoteOpenSuppressed.Contains(frame.TransferId))
                {
                    if (ShouldResumeSuppressedFileTransferDataSessionForV6RecoveryFrame(frame))
                    {
                        fileTransferDataSessionRemoteOpenSuppressed.Remove(frame.TransferId);
                        LocalOperationalLog.Info(
                            "SessionSecurity",
                            $"event=filetransfer_data_session_resume_required_reopened_for_v6_recovery_frame; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason=v6_recovery_control_frame");
                    }
                    else
                    {
                        LocalOperationalLog.Warn(
                            "SessionSecurity",
                            $"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason=data_session_resume_required");
                        return;
                    }
                }

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

        session!.Deliver(frame, channel, receivedTransportKind);
    }

    private static void LogFileTransferEnvelopeEvent(
        string direction,
        MsgType messageType,
        string transferId,
        string? source,
        string? effectiveTransport = null)
    {
        if (messageType == MsgType.FileTransferChunk)
        {
            return;
        }

        effectiveTransport ??= handlingTunaAcceleratedInboundMessage ? "tuna" : "nkn";
        var accelerated = string.Equals(effectiveTransport, "tuna", StringComparison.Ordinal) ? 1 : 0;
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_envelope_{direction}; transport=nkn; effective_transport={effectiveTransport}; accelerated={accelerated}; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={transferId}; source={source ?? "(none)"}");
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

    private void LogNknInboundEnvelopeReceived(NknIncomingMessage message, Envelope env)
    {
        if (message.Channel != NknBridgeChannel.Bulk &&
            !IsFileTransferMessageType(env.Type))
        {
            return;
        }

        LogNknInboundEnvelopeEvidence("nkn_inbound_envelope_received", message, reason: null, env);
    }

    private void LogNknInboundEnvelopeDrop(NknIncomingMessage message, string reason, Envelope? env)
        => LogNknInboundEnvelopeEvidence("nkn_inbound_envelope_drop", message, reason, env);

    private void LogNknInboundEnvelopeEvidence(
        string eventName,
        NknIncomingMessage message,
        string? reason,
        Envelope? env)
    {
        var expectedSource = ResolveExpectedSourceForEnvelopeDiagnostics(env?.Type);
        var expectedSourceAvailable = string.IsNullOrWhiteSpace(expectedSource) ? 0 : 1;
        var sourceMatchesExpected = expectedSourceAvailable > 0 &&
            AddressMatchesForSessionPolicy(message.Source, expectedSource);
        LocalOperationalLog.Info(
            "NKN.Transport",
            $"event={eventName}; channel={MapBridgeChannel(message.Channel)}; reason={reason ?? "(none)"}; envelope_type={(env is null ? "(none)" : MapEnvelopeTypeForDiagnostics(env.Type))}; payload_len={message.Payload.Length}; envelope_payload_len={(env?.Payload.Length ?? 0)}; msg_id={env?.MessageId ?? "(none)"}; source_len={message.Source.Length}; source_matches_local={(IsSelfSource(message.Source) ? 1 : 0)}; expected_source_available={expectedSourceAvailable}; source_matches_expected={(sourceMatchesExpected ? 1 : 0)}; is_topic={(message.IsTopic ? 1 : 0)}");
    }

    private string? ResolveExpectedSourceForEnvelopeDiagnostics(MsgType? messageType)
    {
        if (messageType is null)
        {
            return null;
        }

        return messageType.Value switch
        {
            MsgType.FileTransferChunk or MsgType.FileTransferDataFrame => ResolveExpectedRemoteBulkPeerAddressForCurrentSession(),
            MsgType.ScreenShareFrame => ResolveExpectedRemoteMediaPeerAddressForCurrentSession(),
            MsgType.ScreenShareStop => ResolveExpectedRemotePeerAddressForCurrentSession(),
            _ => ResolveExpectedRemotePeerAddressForCurrentSession(),
        };
    }

    private static bool IsFileTransferMessageType(MsgType messageType)
        => messageType is MsgType.FileTransferOffer
            or MsgType.FileTransferAccept
            or MsgType.FileTransferDecline
            or MsgType.FileTransferStart
            or MsgType.FileTransferChunk
            or MsgType.FileTransferWindowUpdate
            or MsgType.FileTransferMissingRange
            or MsgType.FileTransferPressureState
            or MsgType.FileTransferCancel
            or MsgType.FileTransferError
            or MsgType.FileTransferComplete
            or MsgType.FileTransferSessionOpen
            or MsgType.FileTransferDataFrame
            or MsgType.FileTransferPauseControl
            or MsgType.FileTransferHeartbeat
            or MsgType.FileTransferTransportEpoch
            or MsgType.FileTransferTransportProbe
            or MsgType.FileTransferRepairProof;

    private static string MapEnvelopeTypeForDiagnostics(MsgType messageType)
        => IsFileTransferMessageType(messageType)
            ? MapSecureFileTransferMessageType(messageType)
            : messageType.ToString();

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

    private FileTransferPauseControlV6 EnsureFileTransferSessionId(FileTransferPauseControlV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferHeartbeatV6 EnsureFileTransferSessionId(FileTransferHeartbeatV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferTransportEpochV6 EnsureFileTransferSessionId(FileTransferTransportEpochV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferTransportProbeV6 EnsureFileTransferSessionId(FileTransferTransportProbeV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferRepairProofV6 EnsureFileTransferSessionId(FileTransferRepairProofV6 message)
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

    private readonly record struct FileTransferTerminalTombstone(
        FileTransferTransportPhase Phase,
        long CompletedUtcTicks);

    private readonly record struct InboundFileTransferDispatchWork(
        long Generation,
        long Order,
        MsgType Type,
        string TransferId,
        bool UsesDedicatedChunkDispatch,
        bool BlocksChunkDispatch,
        Action Dispatch,
        TaskCompletionSource<bool>? LifecycleCompletion);

    private void RegisterActiveFileTransferDataSession(string transferId)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.RegisterActiveFileTransferDataSession(transferId);
        }
    }

    private void RegisterActiveFileTransferRuntime(string transferId)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.RegisterActiveFileTransferRuntime(transferId);
        }
    }

    private void UnregisterActiveFileTransferRuntime(string transferId)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.UnregisterActiveFileTransferRuntime(transferId);
        }
    }

    private void ClearActiveFileTransferRuntimeTrackingLocked(string reason)
    {
        if (client is not RealNknClientAdapter realClient)
        {
            return;
        }

        foreach (var state in fileTransferStates.Values)
        {
            if (!state.IsTerminal && state.Phase is not FileTransferTransportPhase.Offered)
            {
                realClient.ClearActiveFileTransferRuntimeTransfers(reason);
                return;
            }
        }
    }

    private void UnregisterActiveFileTransferDataSession(string transferId)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.UnregisterActiveFileTransferDataSession(transferId);
        }
    }

    private void RemoveFileTransferDataSession(TransportFileTransferDataSession session, bool requireLocalOpenForRemoteDelivery = false, string reason = "disposed")
    {
        var removed = false;
        lock (gate)
        {
            if (requireLocalOpenForRemoteDelivery)
            {
                fileTransferDataSessionRemoteOpenSuppressed.Add(session.TransferId);
            }

            if (fileTransferDataSessions.TryGetValue(session.TransferId, out var current) &&
                ReferenceEquals(current, session))
            {
                fileTransferDataSessions.Remove(session.TransferId);
                removed = true;
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event=filetransfer_data_session_removed; transport=nkn; transfer_id={session.TransferId}; session_id={session.SessionId}; reason={reason}");
            }
        }

        if (removed)
        {
            ClearPendingFileTransferV6Handoffs($"data_session_removed_{reason}", session.SessionId);
        }

        UnregisterActiveFileTransferDataSession(session.TransferId);
    }

    private static byte[] SerializeFileTransferDataFrameForWire(FileTransferDataFrame frame)
        => FileTransferDataFrameCodec.Serialize(frame);

    private sealed class TransportFileTransferDataSession : IFileTransferDataSession
    {
        private const long QueuedControlFrameEstimatedBytes = 1024L;

        private readonly NknSignalingTransport owner;
        private readonly object queueGate = new();
        private readonly Channel<QueuedFileTransferDataFrame> frames = Channel.CreateBounded<QueuedFileTransferDataFrame>(
            new BoundedChannelOptions(FileTransferDataSessionMaxQueuedFrames)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        private int queuedFrameCount;
        private long queuedBytes;
        private int disposed;
        private int activeReader;
        private int available = 1;
        private EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? availabilityChanged;

        public TransportFileTransferDataSession(NknSignalingTransport owner, string sessionId, string transferId)
        {
            this.owner = owner;
            SessionId = sessionId;
            TransferId = transferId;
            owner.RegisterActiveFileTransferDataSession(transferId);
        }

        public string SessionId { get; }

        public string TransferId { get; }

        public bool IsAvailable => Volatile.Read(ref available) != 0;

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public int AvailabilitySubscriberCount
            => availabilityChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                availabilityChanged += value;
                if (!owner.TryReplayPendingFileTransferV6Handoff(this, "subscriber_added"))
                {
                    owner.TryRequestCurrentTunaActivationHandoffForFileTransferSession(this, "subscriber_added");
                }
            }
            remove => availabilityChanged -= value;
        }

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct).ConfigureAwait(false)).Frame;

        public async ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=transport_session_multiple_readers");
            }

            try
            {
                var queuedFrame = await frames.Reader.ReadAsync(ct).ConfigureAwait(false);
                ReleaseQueuedFrame(queuedFrame.EstimatedBytes);
                return queuedFrame.ReceivedFrame;
            }
            finally
            {
                Volatile.Write(ref activeReader, 0);
            }
        }

        public async Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            ArgumentNullException.ThrowIfNull(frame);

            await SendAsyncCore(frame, ct).ConfigureAwait(false);
            owner.TrackOutboundFileTransferDataFrameLifecycle(frame);
        }

        private Task SendAsyncCore(FileTransferDataFrame frame, CancellationToken ct)
        {
            if (frame is FileTransferChunkBatchFrameV4 v4Batch && v4Batch.DataSegments.Count > 0)
            {
                return SendChunkBatchV4Async(v4Batch, ct);
            }

            var serializedFrame = SerializeFileTransferDataFrameForWire(frame);
            if (ShouldSendV4ReceiverFeedbackWithBulkRedundancy(frame))
            {
                return SendDataFrameWithBulkRedundancyAsync(
                    frame,
                    serializedFrame,
                    protocolVersion: FileTransferProtocol.ProtocolVersionV6,
                    bothFailedMessage: "V6 redundant feedback send failed on both lanes.",
                    ct);
            }

            var useBulkLane = ShouldUseBulkLane(frame);

            return owner.SendFileTransferEnvelopeRawAsync(
                MsgType.FileTransferDataFrame,
                TransferId,
                serializedFrame,
                useBulkLane,
                frame.Type,
                ct: ct,
                rawPayloadBytes: GetFrameRawPayloadBytes(frame),
                batchChunkCount: GetFrameBatchChunkCount(frame),
                batchProfile: ResolvePayloadEfficiencyProfileNameForDiagnostics(),
                forceRegularNknBulk: ShouldForceRegularNknBulk(frame));
        }

        private async Task SendDataFrameWithBulkRedundancyAsync(
            FileTransferDataFrame frame,
            byte[] serializedFrame,
            int protocolVersion,
            string bothFailedMessage,
            CancellationToken ct)
        {
            await SendDataFrameWithParallelFirstSuccessBulkRedundancyAsync(
                    frame,
                    serializedFrame,
                    protocolVersion,
                    bothFailedMessage,
                    ct)
                .ConfigureAwait(false);
        }

        private async Task SendDataFrameWithParallelFirstSuccessBulkRedundancyAsync(
            FileTransferDataFrame frame,
            byte[] serializedFrame,
            int protocolVersion,
            string bothFailedMessage,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var controlTask = SendRedundantFeedbackCopyAsync(frame, serializedFrame, useBulkLane: false, lane: "control", protocolVersion, ct);
            var bulkTask = SendRedundantFeedbackCopyAsync(frame, serializedFrame, useBulkLane: true, lane: "bulk", protocolVersion, ct);
            var firstTask = await Task.WhenAny(controlTask, bulkTask).ConfigureAwait(false);
            var first = await firstTask.ConfigureAwait(false);
            var secondTask = ReferenceEquals(firstTask, controlTask) ? bulkTask : controlTask;

            if (first.Succeeded)
            {
                LogFeedbackFirstSuccess(frame, first.Lane, started.ElapsedMilliseconds, firstLaneFailed: false, protocolVersion);
                _ = ObserveRedundantFeedbackCopyAfterFirstSuccessAsync(frame, secondTask, protocolVersion);
                return;
            }

            var second = await secondTask.ConfigureAwait(false);
            if (second.Succeeded)
            {
                LogFeedbackFirstSuccess(frame, second.Lane, started.ElapsedMilliseconds, firstLaneFailed: true, protocolVersion);
                return;
            }

            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event={GetFeedbackEventName(protocolVersion, "both_failed")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; first_lane={first.Lane}; second_lane={second.Lane}; first_error={first.Error?.GetType().Name ?? "(none)"}; second_error={second.Error?.GetType().Name ?? "(none)"}");
            throw first.Error ?? second.Error ?? new InvalidOperationException(bothFailedMessage);
        }

        private async Task<RedundantFeedbackSendResult> SendRedundantFeedbackCopyAsync(
            FileTransferDataFrame frame,
            byte[] serializedFrame,
            bool useBulkLane,
            string lane,
            int protocolVersion,
            CancellationToken ct)
        {
            try
            {
                await owner.SendFileTransferEnvelopeRawAsync(
                        MsgType.FileTransferDataFrame,
                        TransferId,
                        serializedFrame,
                        useBulkLane,
                        frame.Type,
                        ct: ct,
                        rawPayloadBytes: 0,
                        batchChunkCount: 0,
                        batchProfile: ResolvePayloadEfficiencyProfileNameForDiagnostics())
                    .ConfigureAwait(false);

                if (useBulkLane)
                {
                    LocalOperationalLog.Info(
                        "SessionSecurity",
                        $"event={GetFeedbackEventName(protocolVersion, "bulk_sent")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; primary_control_failed=0; send_mode=parallel_first_success");
                }

                return new RedundantFeedbackSendResult(lane, true, null);
            }
            catch (Exception ex)
            {
                if (useBulkLane)
                {
                    LocalOperationalLog.Warn(
                        "SessionSecurity",
                        $"event={GetFeedbackEventName(protocolVersion, "bulk_failed")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; primary_control_failed=0; error={ex.GetType().Name}; send_mode=parallel_first_success");
                }
                else
                {
                    LocalOperationalLog.Warn(
                        "SessionSecurity",
                        $"event={GetFeedbackEventName(protocolVersion, "control_failed")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; error={ex.GetType().Name}; send_mode=parallel_first_success");
                }

                return new RedundantFeedbackSendResult(lane, false, ex);
            }
        }

        private static async Task ObserveRedundantFeedbackCopyAfterFirstSuccessAsync(
            FileTransferDataFrame frame,
            Task<RedundantFeedbackSendResult> copyTask,
            int protocolVersion)
        {
            try
            {
                var result = await copyTask.ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event={GetFeedbackEventName(protocolVersion, "secondary_completed")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; lane={result.Lane}; succeeded={(result.Succeeded ? 1 : 0)}; error={result.Error?.GetType().Name ?? "(none)"}");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event={GetFeedbackEventName(protocolVersion, "secondary_observe_failed")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; error={ex.GetType().Name}");
            }
        }

        private static void LogFeedbackFirstSuccess(
            FileTransferDataFrame frame,
            string lane,
            long elapsedMs,
            bool firstLaneFailed,
            int protocolVersion)
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event={GetFeedbackEventName(protocolVersion, "first_success")}; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; lane={lane}; elapsed_ms={elapsedMs}; first_lane_failed={(firstLaneFailed ? 1 : 0)}");
        }

        private readonly record struct RedundantFeedbackSendResult(string Lane, bool Succeeded, Exception? Error);

        private async Task SendV4RepairChunkBatchWithBulkRedundancyAsync(
            FileTransferChunkBatchFrameV4 batch,
            byte[] serializedFrame,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var controlTask = SendRedundantV4RepairChunkBatchCopyAsync(batch, serializedFrame, useBulkLane: false, lane: "control", ct);
            var bulkTask = SendRedundantV4RepairChunkBatchCopyAsync(batch, serializedFrame, useBulkLane: true, lane: "bulk", ct);
            var firstTask = await Task.WhenAny(controlTask, bulkTask).ConfigureAwait(false);
            var first = await firstTask.ConfigureAwait(false);
            var secondTask = ReferenceEquals(firstTask, controlTask) ? bulkTask : controlTask;

            if (first.Succeeded)
            {
                LogV4RepairDeliveryFirstSuccess(batch, first.Lane, started.ElapsedMilliseconds, firstLaneFailed: false);
                _ = ObserveRedundantV4RepairChunkBatchCopyAfterFirstSuccessAsync(batch, secondTask);
                return;
            }

            var second = await secondTask.ConfigureAwait(false);
            if (second.Succeeded)
            {
                LogV4RepairDeliveryFirstSuccess(batch, second.Lane, started.ElapsedMilliseconds, firstLaneFailed: true);
                return;
            }

            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_v4_repair_delivery_both_failed; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; first_lane={first.Lane}; second_lane={second.Lane}; first_error={first.Error?.GetType().Name ?? "(none)"}; second_error={second.Error?.GetType().Name ?? "(none)"}; batch_profile={ResolveBatchProfileNameForDiagnostics(batch)}; repair_delivery_mode={ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)}");
            throw first.Error ?? second.Error ?? new InvalidOperationException("V4 repair chunk batch send failed on both lanes.");
        }

        private async Task<RedundantFeedbackSendResult> SendRedundantV4RepairChunkBatchCopyAsync(
            FileTransferChunkBatchFrameV4 batch,
            byte[] serializedFrame,
            bool useBulkLane,
            string lane,
            CancellationToken ct)
        {
            var batchProfile = ResolveBatchProfileNameForDiagnostics(batch);
            var rawBytes = batch.DataSegments.Sum(static segment => segment?.Length ?? 0);
            try
            {
                await owner.SendFileTransferEnvelopeRawAsync(
                        MsgType.FileTransferDataFrame,
                        TransferId,
                        serializedFrame,
                        useBulkLane,
                        batch.Type,
                        ct: ct,
                        rawPayloadBytes: rawBytes,
                        batchChunkCount: batch.DataSegments.Count,
                        batchProfile: batchProfile,
                        forceRegularNknBulk: batch.ForceRegularNknBulk && useBulkLane)
                    .ConfigureAwait(false);

                return new RedundantFeedbackSendResult(lane, true, null);
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_v4_repair_delivery_{lane}_failed; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; error={ex.GetType().Name}; batch_profile={batchProfile}; repair_delivery_mode={ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)}");
                return new RedundantFeedbackSendResult(lane, false, ex);
            }
        }

        private static async Task ObserveRedundantV4RepairChunkBatchCopyAfterFirstSuccessAsync(
            FileTransferChunkBatchFrameV4 batch,
            Task<RedundantFeedbackSendResult> copyTask)
        {
            try
            {
                var result = await copyTask.ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event=filetransfer_v4_repair_delivery_secondary_completed; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; lane={result.Lane}; succeeded={(result.Succeeded ? 1 : 0)}; error={result.Error?.GetType().Name ?? "(none)"}; batch_profile={ResolveBatchProfileNameForDiagnostics(batch)}; repair_delivery_mode={ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)}");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_v4_repair_delivery_secondary_observe_failed; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; error={ex.GetType().Name}; batch_profile={ResolveBatchProfileNameForDiagnostics(batch)}; repair_delivery_mode={ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)}");
            }
        }

        private static void LogV4RepairDeliveryFirstSuccess(
            FileTransferChunkBatchFrameV4 batch,
            string lane,
            long elapsedMs,
            bool firstLaneFailed)
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_v4_repair_delivery_first_success; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; lane={lane}; elapsed_ms={elapsedMs}; first_lane_failed={(firstLaneFailed ? 1 : 0)}; batch_profile={ResolveBatchProfileNameForDiagnostics(batch)}; repair_delivery_mode={ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)}");
        }

        private static string GetFeedbackEventName(int protocolVersion, string suffix)
            => suffix switch
            {
                "first_success" => "filetransfer_v4_feedback_first_success",
                "secondary_completed" => "filetransfer_v4_feedback_secondary_completed",
                "secondary_observe_failed" => "filetransfer_v4_feedback_secondary_observe_failed",
                "both_failed" => "filetransfer_v4_feedback_both_failed",
                "bulk_sent" => "filetransfer_v4_feedback_bulk_sent",
                "bulk_failed" => "filetransfer_v4_feedback_bulk_failed",
                "control_failed" => "filetransfer_v4_feedback_control_failed",
                _ => $"filetransfer_v4_feedback_{suffix}",
            };

        private async Task SendChunkBatchV4Async(FileTransferChunkBatchFrameV4 batch, CancellationToken ct)
        {
            byte[]? serializedFrame = null;
            FileTransferTransportBudgetMeasurement budgetMeasurement = default;
            var fitsTransportBudget = false;
            try
            {
                serializedFrame = SerializeFileTransferDataFrameForWire(batch);
                fitsTransportBudget = owner.TryMeasureFileTransferDataFrameTransportBudget(
                        TransferId,
                        MsgType.FileTransferDataFrame,
                        serializedFrame,
                        useBulkLane: true,
                        out budgetMeasurement) &&
                    budgetMeasurement.Fits;
            }
            catch (InvalidOperationException)
            {
                // Oversized V4 batches are split below into smaller V4 sub-batches.
            }

            if (fitsTransportBudget && serializedFrame is not null)
            {
                await SendFittingChunkBatchV4Async(batch, serializedFrame, budgetMeasurement, ct).ConfigureAwait(false);
                return;
            }

            await SendChunkBatchAsFittingV4SubBatchesAsync(batch, "payload_budget_fallback", ct).ConfigureAwait(false);
        }

        private async Task SendFittingChunkBatchV4Async(
            FileTransferChunkBatchFrameV4 batch,
            byte[] serializedFrame,
            FileTransferTransportBudgetMeasurement budgetMeasurement,
            CancellationToken ct)
        {
            var rawBytes = batch.DataSegments.Sum(static segment => segment?.Length ?? 0);
            var finalChunkIndex = batch.StartChunkIndex + batch.DataSegments.Count - 1;
            var batchProfile = ResolveBatchProfileNameForDiagnostics(batch);
            var isRepairBatch = IsV4RepairChunkBatch(batch);
            var useControlBulkRepair = isRepairBatch &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant;
            var lane = useControlBulkRepair ? "control_bulk" : "bulk";
            var repairDeliveryMode = isRepairBatch
                ? ResolveV4RepairDeliveryModeName(batch.RepairDeliveryMode)
                : "none";
            var rawToBridgePayloadRatio = budgetMeasurement.BridgePayloadBytes > 0
                ? rawBytes / (double)budgetMeasurement.BridgePayloadBytes
                : 0D;
            var bridgePayloadFillPercent = budgetMeasurement.BridgePayloadBytes * 100D / FileTransferMaxBridgePayloadBytes;
            LocalOperationalLog.Info(
                "SessionSecurity",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={0}; session_id={1}; frame_type={2}; chunk_range={3}-{4}; chunk_frame_count={5}; batch_chunk_count={5}; raw_bytes={6}; lane={7}; batch_profile={8}; repair_delivery_mode={9}; raw_to_bridge_payload_ratio={10:F3}; bridge_payload_fill_percent={11:F2}",
                    batch.TransferId,
                    batch.SessionId,
                    batch.Type,
                    batch.StartChunkIndex,
                    finalChunkIndex,
                    batch.DataSegments.Count,
                    rawBytes,
                    lane,
                    batchProfile,
                    repairDeliveryMode,
                    rawToBridgePayloadRatio,
                    bridgePayloadFillPercent));

            if (useControlBulkRepair)
            {
                await SendV4RepairChunkBatchWithBulkRedundancyAsync(batch, serializedFrame, ct).ConfigureAwait(false);
                return;
            }

            await owner.SendFileTransferEnvelopeRawAsync(
                    MsgType.FileTransferDataFrame,
                    TransferId,
                    serializedFrame,
                    useBulkLane: true,
                    batch.Type,
                    ct: ct,
                    rawPayloadBytes: rawBytes,
                    batchChunkCount: batch.DataSegments.Count,
                    batchProfile: batchProfile,
                    forceRegularNknBulk: batch.ForceRegularNknBulk)
                .ConfigureAwait(false);
        }

        private async Task SendChunkBatchAsFittingV4SubBatchesAsync(
            FileTransferChunkBatchFrameV4 batch,
            string reason,
            CancellationToken ct)
        {
            var perFrameRawBytes = string.Join(",", batch.DataSegments.Select(static segment => segment?.Length ?? 0));
            var finalChunkIndex = batch.StartChunkIndex + batch.DataSegments.Count - 1;
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_chunk_batch_split_for_transport; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; original_frame_type={batch.Type}; split_chunk_range={batch.StartChunkIndex}-{finalChunkIndex}; chunk_frame_count={batch.DataSegments.Count}; per_frame_raw_bytes={perFrameRawBytes}; lane=bulk; reason={reason}");

            var v5MetadataBatch = batch as FileTransferChunkBatchFrameV6;
            var startOffset = 0;
            while (startOffset < batch.DataSegments.Count)
            {
                ct.ThrowIfCancellationRequested();
                FileTransferChunkBatchFrameV4? lastFittingBatch = null;
                byte[]? lastFittingPayload = null;
                FileTransferTransportBudgetMeasurement lastFittingMeasurement = default;
                var endOffsetExclusive = startOffset;
                for (var candidateEndExclusive = startOffset + 1; candidateEndExclusive <= batch.DataSegments.Count; candidateEndExclusive++)
                {
                    var candidateSegments = batch.DataSegments
                        .Skip(startOffset)
                        .Take(candidateEndExclusive - startOffset)
                        .ToArray();
                    var candidateBatch = new FileTransferChunkBatchFrameV6
                    {
                        SessionId = batch.SessionId,
                        TransferId = batch.TransferId,
                        StartChunkIndex = batch.StartChunkIndex + startOffset,
                        ChunkCount = candidateSegments.Length,
                        DataSegments = candidateSegments,
                        BatchProfile = ResolveBatchProfileNameForDiagnostics(batch),
                        RepairDeliveryMode = batch.RepairDeliveryMode,
                        ForceRegularNknBulk = batch.ForceRegularNknBulk,
                        TransportEpoch = v5MetadataBatch?.TransportEpoch ?? 0,
                        BatchId = v5MetadataBatch?.BatchId,
                        RepairRequestId = v5MetadataBatch?.RepairRequestId,
                        Priority = v5MetadataBatch?.Priority,
                        RecoveryMode = v5MetadataBatch?.RecoveryMode,
                    };
                    byte[] candidatePayload;
                    try
                    {
                        candidatePayload = FileTransferDataFrameCodec.Serialize(candidateBatch);
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    if (!owner.TryMeasureFileTransferDataFrameTransportBudget(
                            TransferId,
                            MsgType.FileTransferDataFrame,
                            candidatePayload,
                            useBulkLane: true,
                            out var candidateMeasurement) ||
                        !candidateMeasurement.Fits)
                    {
                        break;
                    }

                    lastFittingBatch = candidateBatch;
                    lastFittingPayload = candidatePayload;
                    lastFittingMeasurement = candidateMeasurement;
                    endOffsetExclusive = candidateEndExclusive;
                }

            if (lastFittingBatch is null || lastFittingPayload is null)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_chunk_batch_split_no_fit; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; frame_type={batch.Type}; chunk_index={batch.StartChunkIndex + startOffset}; raw_bytes={batch.DataSegments[startOffset]?.Length ?? 0}; lane=bulk; reason=payload_budget_or_transport_context_unavailable");
                throw new InvalidOperationException("A V6 chunk segment could not fit inside the transport payload budget or the NKN transport context was unavailable.");
            }

                await SendFittingChunkBatchV4Async(
                        lastFittingBatch,
                        lastFittingPayload,
                        lastFittingMeasurement,
                        ct)
                    .ConfigureAwait(false);
                startOffset = endOffsetExclusive;
            }
        }

        private static bool IsV4RepairChunkBatch(FileTransferChunkBatchFrameV4 batch)
            => batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant ||
               batch.BatchProfile.StartsWith("v4_repair_", StringComparison.OrdinalIgnoreCase);

        private static string ResolveV4RepairDeliveryModeName(FileTransferV4RepairDeliveryMode mode)
            => mode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant
                ? "control_bulk_escalated"
                : "bulk_only";

        public void Deliver(
            FileTransferDataFrame frame,
            NknBridgeChannel channel,
            FileTransferTransportKind receivedTransportKind = FileTransferTransportKind.Unknown)
        {
            if (disposed != 0)
            {
                return;
            }

            var estimatedBytes = EstimateQueuedFrameBytes(frame);
            if (!TryReserveQueuedFrame(estimatedBytes, out var queuedFramesAfter, out var queuedBytesAfter))
            {
                FailQueueOverflow(frame, channel, estimatedBytes);
                return;
            }

            var receivedFrame = new FileTransferReceivedDataFrame(
                frame,
                receivedTransportKind == FileTransferTransportKind.Unknown
                    ? ResolveInboundFileTransferDataFrameTransportKind(channel)
                    : receivedTransportKind,
                MapBridgeChannel(channel),
                DateTimeOffset.UtcNow);
            if (!frames.Writer.TryWrite(new QueuedFileTransferDataFrame(receivedFrame, estimatedBytes)))
            {
                ReleaseQueuedFrame(estimatedBytes);
                if (disposed == 0)
                {
                    FailQueueOverflow(frame, channel, estimatedBytes);
                }

                return;
            }

            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; lane={MapBridgeChannel(channel)}; queued_frames={queuedFramesAfter}; queued_bytes={queuedBytesAfter}");
        }

        private bool TryReserveQueuedFrame(long estimatedBytes, out int queuedFramesAfter, out long queuedBytesAfter)
        {
            lock (queueGate)
            {
                queuedFramesAfter = queuedFrameCount;
                queuedBytesAfter = queuedBytes;
                if (disposed != 0)
                {
                    return false;
                }

                if (queuedFrameCount >= FileTransferDataSessionMaxQueuedFrames ||
                    queuedBytes > FileTransferDataSessionMaxQueuedBytes - estimatedBytes)
                {
                    return false;
                }

                queuedFrameCount++;
                queuedBytes += estimatedBytes;
                queuedFramesAfter = queuedFrameCount;
                queuedBytesAfter = queuedBytes;
                return true;
            }
        }

        private void ReleaseQueuedFrame(long estimatedBytes)
        {
            lock (queueGate)
            {
                if (queuedFrameCount > 0)
                {
                    queuedFrameCount--;
                }

                queuedBytes = Math.Max(0L, queuedBytes - estimatedBytes);
            }
        }

        private void FailQueueOverflow(FileTransferDataFrame frame, NknBridgeChannel channel, long estimatedBytes)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            int queuedFramesSnapshot;
            long queuedBytesSnapshot;
            lock (queueGate)
            {
                queuedFramesSnapshot = queuedFrameCount;
                queuedBytesSnapshot = queuedBytes;
            }

            Interlocked.Exchange(ref available, 0);
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_data_session_overflow; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; lane={MapBridgeChannel(channel)}; queued_frames={queuedFramesSnapshot}; queued_bytes={queuedBytesSnapshot}; frame_estimated_bytes={estimatedBytes}; reason={FileTransferResultCodes.ReceiverBufferExhausted}");

            availabilityChanged?.Invoke(
                this,
                new FileTransferDataSessionAvailabilityChangedEventArgs(
                    isAvailable: false,
                    reason: FileTransferResultCodes.ReceiverBufferExhausted,
                    requiresResumeRequest: true));

            frames.Writer.TryComplete(new InvalidOperationException(FileTransferResultCodes.ReceiverBufferExhausted));
            owner.RemoveFileTransferDataSession(
                this,
                requireLocalOpenForRemoteDelivery: true,
                reason: FileTransferResultCodes.ReceiverBufferExhausted);
        }

        private static long EstimateQueuedFrameBytes(FileTransferDataFrame frame)
        {
            if (frame is not FileTransferChunkBatchFrameV4 batch)
            {
                return QueuedControlFrameEstimatedBytes;
            }

            long rawBytes = QueuedControlFrameEstimatedBytes;
            foreach (var segment in batch.DataSegments)
            {
                rawBytes += segment?.Length ?? 0;
                if (rawBytes >= FileTransferDataSessionMaxQueuedBytes)
                {
                    return FileTransferDataSessionMaxQueuedBytes;
                }
            }

            return rawBytes;
        }

        public void SetAvailability(
            bool isAvailable,
            string reason,
            bool requiresResumeRequest,
            FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
            FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown)
        {
            if (disposed != 0)
            {
                return;
            }

            var next = isAvailable ? 1 : 0;
            var previous = Interlocked.Exchange(ref available, next);
            var explicitHandoff = requiresResumeRequest &&
                                  handoffKind != FileTransferTransportHandoffKind.None;
            if (previous == next && !explicitHandoff)
            {
                return;
            }

            var handler = availabilityChanged;
            var subscriberCount = handler?.GetInvocationList().Length ?? 0;
            if (requiresResumeRequest || explicitHandoff)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_data_session_availability_invoking; transfer_id={TransferId}; session_id={SessionId}; is_available={(isAvailable ? 1 : 0)}; previous_available={(previous != 0 ? 1 : 0)}; reason={SanitizeLogToken(reason)}; requires_resume_request={(requiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; subscriber_count={subscriberCount}");
            }

            if (handler is null)
            {
                if (requiresResumeRequest || explicitHandoff)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_data_session_availability_no_subscribers; transfer_id={TransferId}; session_id={SessionId}; is_available={(isAvailable ? 1 : 0)}; reason={SanitizeLogToken(reason)}; requires_resume_request={(requiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}");
                }

                return;
            }

            handler.Invoke(
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
            if (disposed != 0 ||
                handoffKind == FileTransferTransportHandoffKind.None)
            {
                return;
            }

            var handler = availabilityChanged;
            var subscriberCount = handler?.GetInvocationList().Length ?? 0;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_data_session_handoff_invoking; transfer_id={TransferId}; session_id={SessionId}; is_available={(IsAvailable ? 1 : 0)}; reason={SanitizeLogToken(reason)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; subscriber_count={subscriberCount}");

            if (handler is null)
            {
                owner.TryRecordPendingFileTransferV6Handoff(
                    SessionId,
                    reason,
                    handoffKind,
                    targetTransport,
                    "session_no_subscribers");
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=filetransfer_data_session_handoff_no_subscribers; transfer_id={TransferId}; session_id={SessionId}; reason={SanitizeLogToken(reason)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(handoffKind)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}");
                return;
            }

            handler.Invoke(
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
            owner.RemoveFileTransferDataSession(this);
        }

        private readonly record struct QueuedFileTransferDataFrame(FileTransferReceivedDataFrame ReceivedFrame, long EstimatedBytes);
    }

    private static string GetFileTransferDataFrameChunkIndex(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };

    private static bool ShouldResumeSuppressedFileTransferDataSessionForV6RecoveryFrame(FileTransferDataFrame frame)
        => frame.Type is FileTransferProtocol.ReceiverStateFrameTypeV6
            or FileTransferProtocol.TransportEpochFrameTypeV6
            or FileTransferProtocol.TransportProbeFrameTypeV6
            or FileTransferProtocol.FrontierRequestFrameTypeV6
            or FileTransferProtocol.RepairProofFrameTypeV6;

    private static bool ShouldUseBulkLane(FileTransferDataFrame frame)
        => frame is FileTransferChunkBatchFrameV4
            or FileTransferTransportProbeFrameV6;

    private static bool ShouldForceRegularNknBulk(FileTransferDataFrame frame)
        => frame is FileTransferTransportProbeFrameV6 probe &&
           string.Equals(probe.TargetTransport, "regular_nkn", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSendV4ReceiverFeedbackWithBulkRedundancy(FileTransferDataFrame frame)
    {
        if (IsV4ReceiverFeedbackBulkRedundancyDisabled())
        {
            return false;
        }

        return frame is FileTransferStateFrameV4
            or FileTransferPauseControlFrameV4
            or FileTransferCompleteFrameV4
            or FileTransferCancelFrameV4
            or FileTransferErrorFrameV4
            or FileTransferTransportEpochFrameV6
            or FileTransferFrontierRequestFrameV6
            or FileTransferRepairProofFrameV6;
    }

    private static bool IsV4ReceiverFeedbackBulkRedundancyDisabled()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", category: "filetransfer_tuning");
        return string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFrameRawPayloadBytes(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => batch.DataSegments.Sum(static segment => segment?.Length ?? 0),
            _ => 0,
        };

    private static int GetFrameBatchChunkCount(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => batch.DataSegments.Count,
            _ => 0,
        };

    private static string ResolveBatchProfileNameForDiagnostics(FileTransferChunkBatchFrameV4 batch)
        => string.IsNullOrWhiteSpace(batch.BatchProfile)
            ? "v4_default_21k"
            : batch.BatchProfile;

    private static string ResolvePayloadEfficiencyProfileNameForDiagnostics()
        => FileTransferPayloadEfficiencyProfile.ResolveRequestedFromEnvironment(out _).Name;

    private static void LogFileTransferPayloadBudget(
        string transferId,
        MsgType messageType,
        string? frameType,
        string lane,
        int serializedPayloadBytes,
        int securePayloadBytes,
        int bridgePayloadBytes,
        int bridgeCommandBytes,
        int rawPayloadBytes,
        int batchChunkCount,
        string? batchProfile,
        bool rejected)
    {
        if (messageType != MsgType.FileTransferDataFrame ||
            !string.Equals(frameType, FileTransferProtocol.ChunkBatchFrameTypeV6, StringComparison.Ordinal))
        {
            return;
        }

        var suffix = string.Empty;
        if (rawPayloadBytes > 0 || batchChunkCount > 0 || !string.IsNullOrWhiteSpace(batchProfile))
        {
            var rawToBridgePayloadRatio = bridgePayloadBytes > 0 && rawPayloadBytes > 0
                ? rawPayloadBytes / (double)bridgePayloadBytes
                : 0D;
            var bridgePayloadFillPercent = bridgePayloadBytes * 100D / FileTransferMaxBridgePayloadBytes;
            suffix = string.Format(
                CultureInfo.InvariantCulture,
                "; batch_profile={0}; batch_chunk_count={1}; raw_to_bridge_payload_ratio={2:F3}; bridge_payload_fill_percent={3:F2}",
                string.IsNullOrWhiteSpace(batchProfile) ? "(none)" : batchProfile,
                batchChunkCount,
                rawToBridgePayloadRatio,
                bridgePayloadFillPercent);
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event={(rejected ? "filetransfer_transport_payload_rejected" : "filetransfer_transport_payload_budget")}; transport=nkn; transfer_id={transferId}; message_type={MapSecureFileTransferMessageType(messageType)}; frame_type={frameType}; lane={lane}; serialized_payload_bytes={serializedPayloadBytes}; secure_payload_bytes={securePayloadBytes}; bridge_payload_bytes={bridgePayloadBytes}; bridge_command_bytes={bridgeCommandBytes}; max_allowed_bytes={FileTransferMaxBridgePayloadBytes}{suffix}");
    }

    private readonly record struct FileTransferTransportBudgetMeasurement(
        int SerializedPayloadBytes,
        int SecurePayloadBytes,
        int BridgePayloadBytes,
        int BridgeCommandBytes,
        bool Fits);
}
