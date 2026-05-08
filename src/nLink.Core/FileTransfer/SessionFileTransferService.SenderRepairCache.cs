using System.Diagnostics;
using System.IO;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private void InitializeOutboundSenderRepairCachePolicy(OutboundTransferContext context, bool sourceCanSeek)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.PullSourceCanSeek = sourceCanSeek;
            context.PullSentChunkCache.Clear();
            context.PullSentChunkCacheBytes = 0;
            context.PullSenderCachePressureActive = false;
            context.PullSenderCachePressureEnterLogged = false;
            context.PullSenderCachePressureLastWarnUtc = null;
            context.PullSenderCachePressureLastWarnAcceptedChunks = 0;
            context.PullSenderCachePressureSuppressedCount = 0;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_policy; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(sourceCanSeek ? 1 : 0)}; seekable_target_bytes={SenderRepairCacheSeekableTargetBytes}; seekable_hard_limit_bytes={SenderRepairCacheSeekableHardLimitBytes}; non_seekable_hard_limit_bytes={SenderRepairCacheNonSeekableHardLimitBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(sourceCanSeek)}");
    }

    private static int GetExpectedOutboundChunkLength(OutboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= context.ChunkCount || context.ChunkSizeBytes <= 0)
        {
            return 0;
        }

        var offset = (long)chunkIndex * context.ChunkSizeBytes;
        var remaining = Math.Max(0, context.FileSizeBytes - offset);
        return (int)Math.Min(context.ChunkSizeBytes, remaining);
    }

    private static long GetSenderRepairCacheHardLimitBytes(bool sourceCanSeek)
        => sourceCanSeek ? SenderRepairCacheSeekableHardLimitBytes : SenderRepairCacheNonSeekableHardLimitBytes;

    private static bool TryGetCachedChunkLocked(OutboundTransferContext context, int chunkIndex, out byte[] chunkBytes)
        => context.PullSentChunkCache.TryGetValue(chunkIndex, out chunkBytes!);

    private void StoreSentChunkInCacheLocked(OutboundTransferContext context, int chunkIndex, byte[] chunkBytes)
    {
        if (context.PullSentChunkCache.TryGetValue(chunkIndex, out var existing))
        {
            context.PullSentChunkCacheBytes -= existing.Length;
        }

        context.PullSentChunkCache[chunkIndex] = chunkBytes;
        context.PullSentChunkCacheBytes += chunkBytes.Length;
        TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
        EnforceSenderRepairCacheLimitLocked(context, chunkIndex);
    }

    private void EnforceSenderRepairCacheLimitLocked(OutboundTransferContext context, int protectedChunkIndex)
    {
        var hardLimitBytes = GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek);
        if (context.PullSentChunkCacheBytes <= hardLimitBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
            return;
        }

        if (!context.PullSourceCanSeek)
        {
            LogSenderRepairCacheFailureLocked(context, protectedChunkIndex, SenderCacheExhaustedErrorCode, "non_seekable_cache_limit");
            throw new SenderCacheException(SenderCacheExhaustedErrorCode, "Sender repair cache exceeded the non-seekable source limit.");
        }

        MaybeLogSenderRepairCachePressureEnterLocked(context, "seekable_cache_hard_limit");
        var evicted = 0;
        foreach (var chunkIndex in context.PullSentChunkCache.Keys
                     .OrderBy(chunkIndex => context.LastChunkSentUtc.TryGetValue(chunkIndex, out var sentUtc) ? sentUtc : DateTimeOffset.MinValue)
                     .ThenBy(static chunkIndex => chunkIndex)
                     .ToArray())
        {
            if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
            {
                break;
            }

            if (chunkIndex == protectedChunkIndex)
            {
                continue;
            }

            if (context.PullSentChunkCache.Remove(chunkIndex, out var removedBytes))
            {
                context.PullSentChunkCacheBytes -= removedBytes.Length;
                evicted++;
            }
        }

        if (evicted > 0)
        {
            context.PullSenderCacheEvictionCountRecent += evicted;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_sender_repair_cache_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={hardLimitBytes}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; cache_eviction_count={evicted}; reason=evicted_to_target");
        }

        if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
        }
    }

    private void TrimSenderRepairCacheLocked(OutboundTransferContext context, int nextExpectedChunkIndex)
    {
        if (context.PullSentChunkCache.Count == 0)
        {
            return;
        }

        foreach (var obsoleteChunkIndex in context.PullSentChunkCache.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            if (context.PullSentChunkCache.Remove(obsoleteChunkIndex, out var removedBytes))
            {
                context.PullSentChunkCacheBytes -= removedBytes.Length;
            }
        }

        if (context.PullSentChunkCacheBytes < 0)
        {
            context.PullSentChunkCacheBytes = 0;
        }

        if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
        }
    }

    private void MaybeLogSenderRepairCachePressureEnterLocked(OutboundTransferContext context, string reason)
    {
        if (context.PullSenderCachePressureActive)
        {
            return;
        }

        context.PullSenderCachePressureActive = true;
        var now = DateTimeOffset.UtcNow;
        var acceptedChunkDelta = Math.Abs(context.ChunksAcceptedForTransport - context.PullSenderCachePressureLastWarnAcceptedChunks);
        var shouldWarn = context.PullSenderCachePressureLastWarnUtc is not DateTimeOffset lastWarnUtc ||
                         now - lastWarnUtc >= TimeSpan.FromMilliseconds(SenderRepairCachePressureWarnMinIntervalMs) ||
                         acceptedChunkDelta >= SenderRepairCachePressureWarnMinAcceptedChunkDelta;
        context.PullSenderCachePressureEnterLogged = shouldWarn;
        if (!shouldWarn)
        {
            context.PullSenderCachePressureSuppressedCount++;
            return;
        }

        var suppressedCount = context.PullSenderCachePressureSuppressedCount;
        context.PullSenderCachePressureSuppressedCount = 0;
        context.PullSenderCachePressureLastWarnUtc = now;
        context.PullSenderCachePressureLastWarnAcceptedChunks = context.ChunksAcceptedForTransport;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_pressure_entered; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek)}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; suppressed_count={suppressedCount}");
    }

    private void MaybeLogSenderRepairCachePressureExitLocked(OutboundTransferContext context)
    {
        if (!context.PullSenderCachePressureActive)
        {
            return;
        }

        context.PullSenderCachePressureActive = false;
        if (!context.PullSenderCachePressureEnterLogged)
        {
            return;
        }

        context.PullSenderCachePressureEnterLogged = false;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_pressure_exited; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; suppressed_count={context.PullSenderCachePressureSuppressedCount}");
    }

    private void LogSenderRepairCacheFailureLocked(OutboundTransferContext context, int chunkIndex, string errorCode, string reason)
    {
        var eventName = string.Equals(errorCode, SenderCacheExhaustedErrorCode, StringComparison.Ordinal)
            ? "filetransfer_sender_cache_exhausted"
            : "filetransfer_sender_repair_unavailable";
        LocalOperationalLog.Error(
            "FileTransferService",
            $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; chunk_index={chunkIndex}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek)}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; error_code={errorCode}");
    }

    private List<int> FilterRepairChunkIndicesForSend(
        OutboundTransferContext context,
        IEnumerable<int> requestedChunkIndices,
        bool allowEmergencyCreditRepair,
        int emergencyCreditEndExclusive,
        out RepairChunkFilterStats stats)
    {
        var valid = new List<int>();
        var skippedObsolete = 0;
        var skippedFuture = 0;
        var skippedOutOfBounds = 0;
        var accepted = 0;
        var remoteNextExpected = 0;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                stats = new RepairChunkFilterStats(0, 0, 0, 0, 0);
                return valid;
            }

            accepted = context.ChunksAcceptedForTransport;
            remoteNextExpected = context.RemoteNextExpectedChunkIndex;
            foreach (var chunkIndex in requestedChunkIndices)
            {
                if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
                {
                    skippedOutOfBounds++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "out_of_bounds");
                    continue;
                }

                if (chunkIndex < remoteNextExpected)
                {
                    skippedObsolete++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "obsolete");
                    continue;
                }

                var emergencyCreditAllowed = allowEmergencyCreditRepair &&
                    (chunkIndex == remoteNextExpected ||
                     (emergencyCreditEndExclusive > remoteNextExpected &&
                      chunkIndex >= remoteNextExpected &&
                      chunkIndex < emergencyCreditEndExclusive));
                if (chunkIndex >= accepted && !emergencyCreditAllowed)
                {
                    skippedFuture++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "not_yet_sent");
                    continue;
                }

                valid.Add(chunkIndex);
            }
        }

        stats = new RepairChunkFilterStats(remoteNextExpected, accepted, skippedObsolete, skippedFuture, skippedOutOfBounds);
        return valid;
    }

    private void LogSenderRepairChunkSkippedLocked(OutboundTransferContext context, int chunkIndex, string reason)
    {
        context.PullSenderRepairChunkSkippedCountRecent++;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_chunk_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; chunk_index={chunkIndex}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; chunk_count={context.ChunkCount}");
    }

    private async Task<byte[]> LoadChunkBytesForSendAsync(
        OutboundTransferContext context,
        Stream stream,
        int chunkIndex,
        byte[] buffer,
        bool repairSend)
    {
        if (repairSend)
        {
            return await TryLoadRepairChunkAsync(context, stream, chunkIndex, buffer).ConfigureAwait(false);
        }

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                TryGetCachedChunkLocked(context, chunkIndex, out var cachedBytes))
            {
                return cachedBytes;
            }
        }

        var chunkBytes = await ReadChunkExactAsync(context, stream, chunkIndex, buffer, seekBeforeRead: stream.CanSeek).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                StoreSentChunkInCacheLocked(context, chunkIndex, chunkBytes);
            }
        }

        return chunkBytes;
    }

    private async Task<byte[]> TryLoadRepairChunkAsync(OutboundTransferContext context, Stream stream, int chunkIndex, byte[] buffer)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                throw new OperationCanceledException(context.LifetimeCts.Token);
            }

            if (TryGetCachedChunkLocked(context, chunkIndex, out var cachedBytes))
            {
                context.PullSenderCacheHitCountRecent++;
                return cachedBytes;
            }

            context.PullSenderCacheMissCountRecent++;
            if (!context.PullSourceCanSeek)
            {
                LogSenderRepairCacheFailureLocked(context, chunkIndex, SenderRepairUnavailableErrorCode, "non_seekable_cache_miss");
                throw new SenderCacheException(SenderRepairUnavailableErrorCode, "Requested repair chunk is no longer available from the non-seekable source cache.");
            }
        }

        var chunkBytes = await ReadChunkExactAsync(context, stream, chunkIndex, buffer, seekBeforeRead: true).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.PullSenderSourceRereadCountRecent++;
                StoreSentChunkInCacheLocked(context, chunkIndex, chunkBytes);
            }
        }

        return chunkBytes;
    }

    private async Task<byte[]> ReadChunkExactAsync(
        OutboundTransferContext context,
        Stream stream,
        int chunkIndex,
        byte[] buffer,
        bool seekBeforeRead)
    {
        var readStarted = Stopwatch.GetTimestamp();
        var expectedLength = GetExpectedOutboundChunkLength(context, chunkIndex);
        try
        {
            if (expectedLength <= 0)
            {
                throw new InvalidOperationException("Requested chunk is outside the declared file size.");
            }

            var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
            if (seekBeforeRead)
            {
                if (!stream.CanSeek)
                {
                    throw new InvalidOperationException("Source stream must be seekable for random chunk reads.");
                }

                if (stream.Position != fileOffset)
                {
                    stream.Seek(fileOffset, SeekOrigin.Begin);
                }
            }

            var totalRead = 0;
            while (totalRead < expectedLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, expectedLength - totalRead), context.LifetimeCts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new InvalidOperationException("Source stream did not match the declared file size.");
                }

                totalRead += read;
            }

            var chunkBytes = new byte[expectedLength];
            Buffer.BlockCopy(buffer, 0, chunkBytes, 0, expectedLength);
            return chunkBytes;
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullSenderFeedSourceReadErrorCountRecent++;
                }
            }

            throw;
        }
        finally
        {
            var readDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullSenderFeedReadDurationMsRecent += readDurationMs;
                }
            }
        }
    }

    private readonly record struct RepairChunkFilterStats(
        int RemoteNextExpectedChunkIndex,
        int ChunksAcceptedForTransport,
        int SkippedObsoleteCount,
        int SkippedFutureCount,
        int SkippedOutOfBoundsCount);
}
