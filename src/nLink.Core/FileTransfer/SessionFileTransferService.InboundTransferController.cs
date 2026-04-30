namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed class InboundTransferController
    {
        public InboundTransferController(SessionFileTransferService owner)
        {
        }

        public void UpdateOldestGapTrackingLocked(InboundTransferContext context)
        {
        }

        public void UpdateInboundDegradedRepairModeLocked(InboundTransferContext context)
        {
        }

        public void RecordInboundUsefulBulkProgressLocked(InboundTransferContext context, DateTimeOffset now, bool clearGapState)
        {
            context.LastUsefulBulkProgressUtc = now;
            if (clearGapState)
            {
                context.OldestGapStartChunkIndex = null;
            }
        }

        public void RecordInboundContiguousProgressLocked(InboundTransferContext context, DateTimeOffset now, int contiguousProgressChunkCount)
        {
            if (contiguousProgressChunkCount > 0)
            {
                context.LastContiguousProgressUtc = now;
            }
        }

        public void UpdateInboundBulkHealthLocked(InboundTransferContext context)
        {
        }

        public void RefreshHighestBufferedChunkIndexLocked(InboundTransferContext context)
        {
            context.HighestBufferedChunkIndex = context.PendingChunks.Count == 0
                ? context.NextChunkIndex - 1
                : Math.Max(context.PendingChunks.Keys.Max(), context.NextChunkIndex - 1);
        }

        public int GetCreditFrontierLocked(InboundTransferContext context, int highestBufferedChunkIndex)
            => Math.Max(context.NextChunkIndex, highestBufferedChunkIndex + 1);

        public int GetRawTargetGrantedUntilExclusiveLocked(InboundTransferContext context, int creditFrontier)
            => Math.Min(context.ChunkCount, Math.Max(context.NextChunkIndex, creditFrontier));

        public DateTimeOffset MaxDateTimeOffset(params DateTimeOffset?[] values)
            => values.Where(static value => value is not null).DefaultIfEmpty(DateTimeOffset.MinValue).Max()!.Value;

        public int GetCurrentHighestBufferedChunkIndexLocked(InboundTransferContext context)
            => context.PendingChunks.Count == 0
                ? context.NextChunkIndex - 1
                : Math.Max(context.PendingChunks.Keys.Max(), context.NextChunkIndex - 1);

        public int GetEffectiveGrantChunksLocked(InboundTransferContext context)
            => V4MixedScreenShareCreditWindowChunks;

        public int GetEffectiveStartupGrantChunksLocked()
            => V4MixedScreenShareCreditWindowChunks;

        public int GetEffectiveLowWatermarkChunksLocked(InboundTransferContext context)
            => Math.Max(1, V4MixedScreenShareCreditWindowChunks / 2);

        public bool ShouldDeferGrantExtensionDueToGapLocked(InboundTransferContext context, int highestBufferedChunkIndex, int targetGrantedUntilExclusive)
            => false;

        public bool ShouldLogGapDeferredLocked(InboundTransferContext context)
            => false;
    }
}
