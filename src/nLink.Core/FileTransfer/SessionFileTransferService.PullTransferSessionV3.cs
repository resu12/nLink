using System.Buffers;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundPullSendLoopV3Async(OutboundTransferContext context)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            var initialPipelineDepth = ResolveOutboundInitialPipelineDepth(context);
            var startMessage = new FileTransferStartV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                Sha256Base64 = context.Sha256Base64!,
                ChunkCount = context.ChunkCount,
                ChunkSizeBytes = context.ChunkSizeBytes,
            };
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
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
                context.PullV3GrantedUntilExclusive = 0;
                context.PullSentChunkCache.Clear();
            }

            UpdateOutboundState(context, FileTransferTransferState.AwaitingStart, 0, 0, "Starting file transfer.");
            await currentTransport.SendFileTransferStartAsync(startMessage, context.LifetimeCts.Token).ConfigureAwait(false);
            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);

            var manifest = new FileTransferManifestFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                ChunkSizeBytes = context.ChunkSizeBytes,
                ChunkCount = context.ChunkCount,
                Sha256Base64 = context.Sha256Base64!,
            };

            await dataSession.SendAsync(manifest, context.LifetimeCts.Token).ConfigureAwait(false);
            LogPullBinaryFrameSent(context.TransferId, context.SessionId, manifest, payloadBytes: 0);
            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for receiver credit.");

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
                    case FileTransferGrantWindowFrameV3 grant:
                        ApplyOutboundV3Grant(context, grant);
                        await SendGrantedChunksV3Async(context, stream, dataSession).ConfigureAwait(false);
                        break;
                    case FileTransferAckProgressFrameV3 ack:
                        ApplyOutboundV3Ack(context, ack);
                        await SendGrantedChunksV3Async(context, stream, dataSession).ConfigureAwait(false);
                        break;
                    case FileTransferRepairRequestFrameV3 repair:
                        await ResendRequestedChunksV3Async(context, stream, dataSession, repair).ConfigureAwait(false);
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
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame_v3");
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

    private async Task RunInboundPullReceiveLoopV3Async(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
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

            FileTransferManifestFrameV3? manifest = null;
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
                if (frame is FileTransferManifestFrameV3 receivedManifest)
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
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "waiting_for_manifest_v3");
                }
            }

            await InitializeInboundPullManifestV3Async(context, manifest).ConfigureAwait(false);
            await SendInboundGrantWindowV3Async(context, forceGrant: true).ConfigureAwait(false);

            pendingReceiveTask = null;
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
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    await MaybeHandlePullV3RepairTimeoutAsync(context).ConfigureAwait(false);
                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferChunkDataFrameV3 chunk:
                        await HandleInboundPullChunkV3Async(context, chunk).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV3 batch:
                        await HandleInboundPullChunkBatchV3Async(context, batch).ConfigureAwait(false);
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
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_inbound_frame_v3");
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

    private async Task InitializeInboundPullManifestV3Async(InboundTransferContext context, FileTransferManifestFrameV3 manifest)
    {
        await InitializeInboundPullManifestAsync(
            context,
            new FileTransferManifestFrameV2
            {
                SessionId = manifest.SessionId,
                TransferId = manifest.TransferId,
                FileName = manifest.FileName,
                FileSizeBytes = manifest.FileSizeBytes,
                ChunkSizeBytes = manifest.ChunkSizeBytes,
                ChunkCount = manifest.ChunkCount,
                Sha256Base64 = manifest.Sha256Base64,
            }).ConfigureAwait(false);
    }

    private static FileTransferChunkDataFrameV2 CreatePullChunkDataFrame(
        int protocolVersion,
        string sessionId,
        string transferId,
        int chunkIndex,
        int chunkCount,
        byte[] chunkBytes)
        => protocolVersion == FileTransferProtocol.ProtocolVersionV3
            ? new FileTransferChunkDataFrameV3
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = chunkBytes,
            }
            : new FileTransferChunkDataFrameV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = chunkBytes,
            };

    private async Task SendGrantedChunksV3Async(
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

            var startChunk = context.ChunksAcceptedForTransport;
            var grantedUntilExclusive = Math.Min(context.PullV3GrantedUntilExclusive, context.ChunkCount);
            if (grantedUntilExclusive <= startChunk)
            {
                return;
            }

            chunkIndicesToSend = Enumerable.Range(startChunk, grantedUntilExclusive - startChunk).ToList();
        }

        await SendChunkIndicesV3Async(context, stream, dataSession, chunkIndicesToSend).ConfigureAwait(false);
    }

    private async Task ResendRequestedChunksV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        FileTransferRepairRequestFrameV3 repair)
    {
        var startChunkIndex = Math.Max(0, repair.StartChunkIndex);
        var requestedChunkCount = Math.Max(1, repair.RequestedChunkCount);
        var endChunkExclusive = Math.Min(context.ChunkCount, startChunkIndex + requestedChunkCount);
        if (endChunkExclusive <= startChunkIndex)
        {
            return;
        }

        await SendChunkIndicesV3Async(
            context,
            stream,
            dataSession,
            Enumerable.Range(startChunkIndex, endChunkExclusive - startChunkIndex).ToList()).ConfigureAwait(false);
    }

    private async Task SendChunkIndicesV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        List<int> chunkIndices)
    {
        if (chunkIndices.Count == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            for (var index = 0; index < chunkIndices.Count; index++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                var batchedChunkCount = await TrySendChunkBatchV3Async(context, stream, dataSession, chunkIndices, index, buffer).ConfigureAwait(false);
                if (batchedChunkCount > 0)
                {
                    index += batchedChunkCount - 1;
                    continue;
                }

                var chunkIndex = chunkIndices[index];
                var chunkBytes = await ReadOrLoadChunkBytesAsync(context, stream, chunkIndex, buffer).ConfigureAwait(false);
                var frame = new FileTransferChunkDataFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = context.ChunkCount,
                    Data = chunkBytes,
                };

                await dataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
                LogPullBinaryFrameSent(context.TransferId, context.SessionId, frame, chunkBytes.Length);

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    var sentUtc = DateTimeOffset.UtcNow;
                    context.SentAwaitingAck[chunkIndex] = sentUtc;
                    context.LastChunkSentUtc[chunkIndex] = sentUtc;
                    context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, chunkIndex + 1);
                    context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                    context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                    context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                }
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<int> TrySendChunkBatchV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        IReadOnlyList<int> chunkIndices,
        int startListIndex,
        byte[] buffer)
    {
        if (startListIndex + 1 >= chunkIndices.Count)
        {
            return 0;
        }

        var startChunkIndex = chunkIndices[startListIndex];
        var expectedChunkIndex = startChunkIndex;
        var totalRawBytes = 0;
        List<byte[]> dataSegments = [];
        for (var index = startListIndex; index < chunkIndices.Count && dataSegments.Count < PullV3BatchMaxChunks; index++)
        {
            var chunkIndex = chunkIndices[index];
            if (chunkIndex != expectedChunkIndex)
            {
                break;
            }

            var chunkBytes = await ReadOrLoadChunkBytesAsync(context, stream, chunkIndex, buffer).ConfigureAwait(false);
            if (totalRawBytes + chunkBytes.Length > FileTransferChunkBudget.MaxRawChunkBytes)
            {
                break;
            }

            dataSegments.Add(chunkBytes);
            totalRawBytes += chunkBytes.Length;
            expectedChunkIndex++;
        }

        if (dataSegments.Count < 2)
        {
            return 0;
        }

        var batch = new FileTransferChunkBatchFrameV3
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = startChunkIndex,
            ChunkCount = context.ChunkCount,
            DataSegments = dataSegments,
        };

        _ = FileTransferDataFrameCodec.Serialize(batch);
        await dataSession.SendAsync(batch, context.LifetimeCts.Token).ConfigureAwait(false);
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, batch, totalRawBytes);

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return dataSegments.Count;
            }

            var sentUtc = DateTimeOffset.UtcNow;
            for (var chunkOffset = 0; chunkOffset < dataSegments.Count; chunkOffset++)
            {
                var chunkIndex = startChunkIndex + chunkOffset;
                context.SentAwaitingAck[chunkIndex] = sentUtc;
                context.LastChunkSentUtc[chunkIndex] = sentUtc;
                context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, chunkIndex + 1);
                context.RecentPullChunkSentUtc.Enqueue(sentUtc);
            }

            context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                ? context.FileSizeBytes
                : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
            TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
            context.PullUsefulPayloadBytesRecent += totalRawBytes;
        }

        return dataSegments.Count;
    }

    private async Task<byte[]> ReadOrLoadChunkBytesAsync(OutboundTransferContext context, Stream stream, int chunkIndex, byte[] buffer)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                context.PullSentChunkCache.TryGetValue(chunkIndex, out var cachedBytes))
            {
                return cachedBytes;
            }
        }

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

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.PullSentChunkCache[chunkIndex] = chunkBytes;
            }
        }

        return chunkBytes;
    }

    private void ApplyOutboundV3Grant(OutboundTransferContext context, FileTransferGrantWindowFrameV3 grant)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            ApplyOutboundV3ProgressLocked(context, grant.NextExpectedChunkIndex, grant.BytesCommitted);
            context.PullV3GrantedUntilExclusive = Math.Max(
                grant.NextExpectedChunkIndex,
                Math.Min(context.ChunkCount, grant.GrantedUntilChunkIndexExclusive));
            context.PullV3LastGrantReceivedUtc = DateTimeOffset.UtcNow;
            context.StatusMessage = "Receiver granted more transfer credit.";
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private void ApplyOutboundV3Ack(OutboundTransferContext context, FileTransferAckProgressFrameV3 ack)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            ApplyOutboundV3ProgressLocked(context, ack.NextExpectedChunkIndex, ack.BytesCommitted);
            context.StatusMessage = context.ChunksTransferred >= context.ChunkCount
                ? "Waiting for receiver verification."
                : "Receiver acknowledged streamed chunks.";
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private static void ApplyOutboundV3ProgressLocked(OutboundTransferContext context, int nextExpectedChunkIndex, long bytesCommitted)
    {
        context.ChunksTransferred = Math.Max(context.ChunksTransferred, Math.Min(nextExpectedChunkIndex, context.ChunkCount));
        context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(bytesCommitted, context.FileSizeBytes));
        foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            context.SentAwaitingAck.Remove(chunkIndex);
        }

        foreach (var chunkIndex in context.LastChunkSentUtc.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            context.LastChunkSentUtc.Remove(chunkIndex);
        }

        TrimOutboundPullSentChunkCache(context, nextExpectedChunkIndex);
    }

    private async Task HandleInboundPullChunkV3Async(InboundTransferContext context, FileTransferChunkDataFrameV3 chunk)
    {
        if (chunk.Data.Length == 0 || chunk.Data.Length > FileTransferProtocol.MaxChunkRawBytes)
        {
            throw new InvalidOperationException("Chunk payload exceeded the V3 raw payload budget.");
        }

        await HandleInboundPullChunksAsync(context, [(chunk.ChunkIndex, chunk.Data)]).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunkBatchV3Async(InboundTransferContext context, FileTransferChunkBatchFrameV3 batch)
    {
        var chunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(batch.DataSegments.Count);
        for (var index = 0; index < batch.DataSegments.Count; index++)
        {
            var segment = batch.DataSegments[index];
            if (segment.Length == 0 || segment.Length > FileTransferProtocol.MaxChunkRawBytes)
            {
                throw new InvalidOperationException("Chunk batch payload exceeded the V3 raw payload budget.");
            }

            chunks.Add((batch.StartChunkIndex + index, segment));
        }

        await HandleInboundPullChunksAsync(context, chunks).ConfigureAwait(false);
    }

    private async Task MaybeHandlePullV3RepairTimeoutAsync(InboundTransferContext context)
    {
        FileTransferRepairRequestFrameV3? repair = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0 ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return;
            }

            var lastProgressUtc = context.PullLastProgressUtc ?? context.MetadataAwaitingSinceUtc ?? DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - lastProgressUtc < TimeSpan.FromMilliseconds(GetPullSessionRequestTimeoutMs(context)))
            {
                return;
            }

            repair = new FileTransferRepairRequestFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                StartChunkIndex = context.NextChunkIndex,
                RequestedChunkCount = Math.Min(PullV3RepairRequestChunkCount, context.ChunkCount - context.NextChunkIndex),
            };
            context.PullLastProgressUtc = DateTimeOffset.UtcNow;
            context.PullV3LastRepairRequestSentUtc = context.PullLastProgressUtc;
        }

        await context.DataSession!.SendAsync(repair, context.LifetimeCts.Token).ConfigureAwait(false);
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, repair, payloadBytes: 0);
        await SendInboundGrantWindowV3Async(context, forceGrant: true).ConfigureAwait(false);
    }

    private async Task SendInboundGrantWindowV3Async(InboundTransferContext context, bool forceGrant)
    {
        FileTransferDataFrameV2? frame = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0)
            {
                return;
            }

            _ = UpdateInboundV3WindowProfileLocked(context, DateTimeOffset.UtcNow);
            var targetWindowChunks = ResolveInboundV3TargetWindowChunksLocked(context);
            var targetGrantedUntilExclusive = Math.Min(context.ChunkCount, context.NextChunkIndex + targetWindowChunks);
            var currentCredit = Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex);
            var desiredCredit = Math.Max(0, targetGrantedUntilExclusive - context.NextChunkIndex);
            var shouldGrant = forceGrant || currentCredit <= Math.Max(1, desiredCredit / PullV3GrantLowWatermarkDivisor);
            var shouldAckOnly =
                !shouldGrant &&
                context.PullAckDebtBytes >= PullV3HealthyAckThresholdBytes &&
                (context.PullLastAckSentUtc is null ||
                 DateTimeOffset.UtcNow - context.PullLastAckSentUtc.Value >= TimeSpan.FromMilliseconds(PullV3HealthyAckCoalesceDelayMs));

            if (!shouldGrant && !shouldAckOnly)
            {
                return;
            }

            if (shouldGrant)
            {
                context.PullV3GrantedUntilExclusive = targetGrantedUntilExclusive;
                context.PullV3LastGrantSentUtc = DateTimeOffset.UtcNow;
                frame = new FileTransferGrantWindowFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    GrantedUntilChunkIndexExclusive = context.PullV3GrantedUntilExclusive,
                    BytesCommitted = context.BytesTransferred,
                };
            }
            else
            {
                frame = new FileTransferAckProgressFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    BytesCommitted = context.BytesTransferred,
                };
            }

            var now = DateTimeOffset.UtcNow;
            context.PullLastAckSentUtc = now;
            context.PullLastAckSentChunkIndex = context.NextChunkIndex;
            context.PullAckDebtChunks = 0;
            context.PullAckDebtBytes = 0;
            context.RecentPullAckSentUtc.Enqueue(now);
            MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
        }

        await context.DataSession!.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, frame, payloadBytes: 0);
    }

    private int ResolveInboundV3TargetWindowChunksLocked(InboundTransferContext context)
    {
        var targetBytes = sessionScreenShareDegraded || context.PullSessionDegraded
            ? PullV3DegradedTargetInFlightBytes
            : sessionScreenShareActive
                ? PullV3ScreenshareTargetInFlightBytes
                : context.PullV3ExpandedWindowActive
                    ? PullV3HealthyMaximumTargetInFlightBytes
                    : PullV3HealthyTargetInFlightBytes;
        targetBytes = Math.Min(targetBytes, PullV3HealthyMaximumTargetInFlightBytes);
        return Math.Max(4, (int)Math.Ceiling((double)targetBytes / Math.Max(1, context.ChunkSizeBytes)));
    }

    private string UpdateInboundV3WindowProfileLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (sessionScreenShareActive || sessionScreenShareDegraded)
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullV3LimitedWindowActive = false;
            context.PullV3CleanSinceUtc = null;
            context.PullV3AdverseSinceUtc ??= now;
            return ResolveInboundV3ProfileName(context);
        }

        var recentRepair = context.PullV3LastRepairRequestSentUtc is not null &&
                           now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs);
        var reorderLimited =
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded &&
            !context.PullSessionDegraded &&
            context.PullTimeoutStreak == 0 &&
            !recentRepair &&
            context.PullLateArrivalDistance >= PullV3LimitedReorderDistanceThreshold;
        var healthyEligible =
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded &&
            !context.PullSessionDegraded &&
            !context.PullV3LimitedWindowActive &&
            context.BytesTransferred >= PullV3StepUpProgressBytesThreshold &&
            context.PullTimeoutStreak == 0 &&
            !recentRepair &&
            context.PullLateArrivalDistance < PullV3HighReorderDistanceThreshold;
        var adverse =
            sessionScreenShareActive ||
            sessionScreenShareDegraded ||
            context.PullSessionDegraded ||
            context.PullTimeoutStreak >= 2 ||
            recentRepair ||
            context.PullLateArrivalDistance >= PullV3HighReorderDistanceThreshold;

        if (healthyEligible)
        {
            context.PullV3CleanSinceUtc ??= now;
        }
        else
        {
            context.PullV3CleanSinceUtc = null;
        }

        if (adverse)
        {
            context.PullV3AdverseSinceUtc ??= now;
        }
        else
        {
            context.PullV3AdverseSinceUtc = null;
        }

        if (!context.PullV3LimitedWindowActive &&
            context.PullV3ExpandedWindowActive &&
            reorderLimited &&
            context.PullV3AdverseSinceUtc is not null &&
            now - context.PullV3AdverseSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3LimitedStepDownHoldMs) &&
            (context.PullLastProfileAdjustmentUtc is null ||
             now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullV3LimitedWindowActive = true;
            context.PullLastProfileAdjustmentUtc = now;
        }
        else if (context.PullV3LimitedWindowActive)
        {
            var limitedRecoveryEligible =
                !sessionScreenShareActive &&
                !sessionScreenShareDegraded &&
                !context.PullSessionDegraded &&
                context.PullTimeoutStreak == 0 &&
                !recentRepair &&
                context.PullLateArrivalDistance < PullV3HighReorderDistanceThreshold;
            if (limitedRecoveryEligible &&
                context.PullV3CleanSinceUtc is not null &&
                now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3LimitedRecoveryHoldMs) &&
                (context.PullLastProfileAdjustmentUtc is null ||
                 now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
            {
                context.PullV3LimitedWindowActive = false;
                context.PullLastProfileAdjustmentUtc = now;
            }
        }

        if (!context.PullV3ExpandedWindowActive &&
            healthyEligible &&
            context.PullV3CleanSinceUtc is not null &&
            now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3HealthyStepUpHoldMs) &&
            (context.PullLastProfileAdjustmentUtc is null ||
             now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
        {
            context.PullV3ExpandedWindowActive = true;
            context.PullLastProfileAdjustmentUtc = now;
        }
        else if (context.PullV3ExpandedWindowActive &&
                 adverse &&
                 context.PullV3AdverseSinceUtc is not null &&
                 now - context.PullV3AdverseSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs) &&
                 (context.PullLastProfileAdjustmentUtc is null ||
                  now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullLastProfileAdjustmentUtc = now;
        }

        return ResolveInboundV3ProfileName(context);
    }

    private string ResolveInboundV3ProfileName(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded || context.PullSessionDegraded)
        {
            return "degraded";
        }

        if (sessionScreenShareActive)
        {
            return "balanced_screenshare";
        }

        if (context.PullV3LimitedWindowActive)
        {
            return "healthy_limited";
        }

        return context.PullV3ExpandedWindowActive ? "healthy_expanded" : "healthy";
    }
}
