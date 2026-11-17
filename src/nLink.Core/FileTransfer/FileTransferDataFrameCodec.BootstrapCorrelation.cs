namespace NLink.Core.FileTransfer;

public static partial class FileTransferDataFrameCodec
{
    private static void WriteBootstrapCorrelationFields(Stream stream, FileTransferFallbackBootstrapFrameV6 frame)
    {
        if (frame is FileTransferFallbackBootstrapStateFrameV6 state)
        {
            WriteInt32(stream, state.Epoch);
            WriteInt32(stream, state.ContiguousCommittedChunkIndex);
            WriteInt32(stream, state.DurableReceivedHighestChunkIndex);
            WriteInt32(stream, state.CreditUntilChunkIndexExclusive);
            WriteInt32(stream, 0); // Bootstrap never carries repair ranges.
            WriteInt64(stream, state.BytesCommitted);
            WriteBool(stream, state.ReceiverMemoryPressure);
            WriteBool(stream, state.ReceiverDiskPressure);
            WriteBool(stream, state.TerminalReady);
            WriteBool(stream, state.TransferPaused);
            WriteOptionalString(stream, state.TransferPauseReason);
        }

        // IDs have already passed exact canonical validation; never normalize them.
        WriteOptionalString(stream, frame.SenderRequestId);
        WriteOptionalString(stream, frame.ReceiverChallengeId);
    }

    private static bool TryReadBootstrapCorrelationFrame(
        ref BinaryFrameReader reader,
        byte code,
        string sessionId,
        string transferId,
        TransportMetadata metadata,
        out FileTransferDataFrame? frame)
    {
        frame = null;
        FileTransferFallbackBootstrapFrameV6 candidate;
        if (code == 56)
        {
            if (!reader.TryReadInt32(out var epoch) ||
                !reader.TryReadInt32(out var contiguous) ||
                !reader.TryReadInt32(out var highest) ||
                !reader.TryReadInt32(out var credit) ||
                !reader.TryReadInt32(out var ranges) || ranges != 0 ||
                !reader.TryReadInt64(out var bytes) ||
                !reader.TryReadBool(out var memory) ||
                !reader.TryReadBool(out var disk) ||
                !reader.TryReadBool(out var terminal) ||
                !reader.TryReadBool(out var paused) ||
                !reader.TryReadOptionalString(out var pauseReason))
            {
                return false;
            }
            candidate = new FileTransferFallbackBootstrapStateFrameV6
            {
                Epoch = epoch,
                ContiguousCommittedChunkIndex = contiguous,
                DurableReceivedHighestChunkIndex = highest,
                CreditUntilChunkIndexExclusive = credit,
                BytesCommitted = bytes,
                ReceiverMemoryPressure = memory,
                ReceiverDiskPressure = disk,
                TerminalReady = terminal,
                TransferPaused = paused,
                TransferPauseReason = pauseReason,
            };
        }
        else
        {
            candidate = new FileTransferFallbackBootstrapRequestFrameV6();
        }

        if (!reader.TryReadOptionalString(out var senderId) ||
            !reader.TryReadOptionalString(out var receiverId) || !reader.IsFullyConsumed)
        {
            return false;
        }
        candidate = candidate with
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransportEpoch = metadata.TransportEpoch,
            BatchId = metadata.BatchId,
            RepairRequestId = metadata.RepairRequestId,
            Priority = metadata.Priority,
            RecoveryMode = metadata.RecoveryMode,
            SenderRequestId = senderId,
            ReceiverChallengeId = receiverId,
        };
        if (!FileTransferBootstrapCorrelation.IsValidFrame(candidate))
        {
            return false;
        }
        frame = candidate;
        return true;
    }
}
