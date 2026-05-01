using System.Globalization;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.DevLocal;

public enum DevLocalImpairmentProfile
{
    None = 0,
    DelayJitter,
    ReorderBurst,
    LossBurst,
    ScreenSharePressure,
}

public enum DevLocalImpairmentLane
{
    FileTransferData = 0,
    ScreenShareMedia,
}

public sealed record DevLocalImpairmentOptions(
    DevLocalImpairmentProfile Profile,
    int Seed)
{
    public static DevLocalImpairmentOptions Disabled { get; } = new(DevLocalImpairmentProfile.None, 0);

    public bool IsEnabled => Profile != DevLocalImpairmentProfile.None;
}

public sealed record DevLocalImpairmentDecision(
    DevLocalImpairmentLane Lane,
    DevLocalImpairmentProfile Profile,
    long Sequence,
    bool Drop,
    TimeSpan Delay,
    bool Reordered,
    string Reason,
    string TransferId,
    string FrameType,
    string ChunkIndex,
    int PayloadBytes);

public sealed record DevLocalImpairmentMetricsSnapshot(
    DevLocalImpairmentProfile Profile,
    int Seed,
    long FileTransferDataFramesObserved,
    long FileTransferDataFramesDelayed,
    long FileTransferDataFramesDropped,
    long FileTransferDataFramesReordered,
    long ScreenShareMediaFramesObserved,
    long ScreenShareMediaFramesDelayed,
    long ScreenShareMediaFramesDropped,
    long DelayCount,
    long DropCount,
    long ReorderCount,
    long TotalDelayMilliseconds,
    long MaxDelayMilliseconds);

public sealed class DevLocalImpairmentPolicy
{
    private readonly object gate = new();
    private readonly HashSet<string> droppedFileTransferChunkKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> lossDropsByTransfer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> reorderDelaysByTransfer = new(StringComparer.Ordinal);
    private long sequence;
    private long fileTransferDataFramesObserved;
    private long fileTransferDataFramesDelayed;
    private long fileTransferDataFramesDropped;
    private long fileTransferDataFramesReordered;
    private long screenShareMediaFramesObserved;
    private long screenShareMediaFramesDelayed;
    private long screenShareMediaFramesDropped;
    private long totalDelayMilliseconds;
    private long maxDelayMilliseconds;

    public DevLocalImpairmentPolicy(DevLocalImpairmentOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public DevLocalImpairmentOptions Options { get; }

    public DevLocalImpairmentDecision ObserveFileTransferDataFrame(FileTransferDataFrame frame, string transferId)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var currentSequence = Interlocked.Increment(ref sequence);
        Interlocked.Increment(ref fileTransferDataFramesObserved);

        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId)
            ? frame.TransferId
            : transferId.Trim();
        var frameType = string.IsNullOrWhiteSpace(frame.Type) ? "(none)" : frame.Type;
        var chunkIndex = FormatFileTransferChunkIndex(frame);
        var decision = Options.Profile switch
        {
            DevLocalImpairmentProfile.DelayJitter when IsFileTransferChunkPayload(frame) =>
                BuildDelayDecision(
                    DevLocalImpairmentLane.FileTransferData,
                    currentSequence,
                    normalizedTransferId,
                    frameType,
                    chunkIndex,
                    payloadBytes: EstimateFileTransferPayloadBytes(frame),
                    delayMs: 15 + (StableModulo(currentSequence, 6) * 5),
                    reordered: false,
                    reason: "delay_jitter"),
            DevLocalImpairmentProfile.ReorderBurst when IsFileTransferChunkPayload(frame) &&
                                                        IsReorderBurstCandidate(currentSequence, frame) =>
                BuildDelayDecision(
                    DevLocalImpairmentLane.FileTransferData,
                    currentSequence,
                    normalizedTransferId,
                    frameType,
                    chunkIndex,
                    payloadBytes: EstimateFileTransferPayloadBytes(frame),
                    delayMs: 140 + (StableModulo(currentSequence, 5) * 20),
                    reordered: true,
                    reason: "reorder_burst"),
            DevLocalImpairmentProfile.LossBurst when IsFileTransferChunkPayload(frame) &&
                                                     ShouldDropLossBurstFrame(frame, normalizedTransferId, currentSequence) =>
                new DevLocalImpairmentDecision(
                    DevLocalImpairmentLane.FileTransferData,
                    Options.Profile,
                    currentSequence,
                    Drop: true,
                    Delay: TimeSpan.Zero,
                    Reordered: false,
                    Reason: "loss_burst_first_send",
                    TransferId: normalizedTransferId,
                    FrameType: frameType,
                    ChunkIndex: chunkIndex,
                    PayloadBytes: EstimateFileTransferPayloadBytes(frame)),
            _ => NoopDecision(
                DevLocalImpairmentLane.FileTransferData,
                currentSequence,
                normalizedTransferId,
                frameType,
                chunkIndex,
                EstimateFileTransferPayloadBytes(frame)),
        };

        RecordAndLogDecision(decision);
        return decision;
    }

    public DevLocalImpairmentDecision ObserveScreenShareMediaPayload(int payloadBytes)
    {
        var currentSequence = Interlocked.Increment(ref sequence);
        Interlocked.Increment(ref screenShareMediaFramesObserved);

        var delayMs = Options.Profile switch
        {
            DevLocalImpairmentProfile.DelayJitter => 10 + (StableModulo(currentSequence, 5) * 5),
            DevLocalImpairmentProfile.ScreenSharePressure => 85 + (StableModulo(currentSequence, 4) * 15),
            _ => 0,
        };

        var decision = delayMs > 0
            ? BuildDelayDecision(
                DevLocalImpairmentLane.ScreenShareMedia,
                currentSequence,
                transferId: "(none)",
                frameType: "screenshare_payload",
                chunkIndex: "(none)",
                payloadBytes: Math.Max(0, payloadBytes),
                delayMs,
                reordered: false,
                reason: Options.Profile == DevLocalImpairmentProfile.ScreenSharePressure
                    ? "screenshare_pressure"
                    : "delay_jitter")
            : NoopDecision(
                DevLocalImpairmentLane.ScreenShareMedia,
                currentSequence,
                transferId: "(none)",
                frameType: "screenshare_payload",
                chunkIndex: "(none)",
                payloadBytes: Math.Max(0, payloadBytes));

        RecordAndLogDecision(decision);
        return decision;
    }

    public DevLocalImpairmentMetricsSnapshot GetSnapshot()
    {
        var ftDelayed = Interlocked.Read(ref fileTransferDataFramesDelayed);
        var ftDropped = Interlocked.Read(ref fileTransferDataFramesDropped);
        var ftReordered = Interlocked.Read(ref fileTransferDataFramesReordered);
        var ssDelayed = Interlocked.Read(ref screenShareMediaFramesDelayed);
        var ssDropped = Interlocked.Read(ref screenShareMediaFramesDropped);
        return new DevLocalImpairmentMetricsSnapshot(
            Options.Profile,
            Options.Seed,
            Interlocked.Read(ref fileTransferDataFramesObserved),
            ftDelayed,
            ftDropped,
            ftReordered,
            Interlocked.Read(ref screenShareMediaFramesObserved),
            ssDelayed,
            ssDropped,
            ftDelayed + ssDelayed,
            ftDropped + ssDropped,
            ftReordered,
            Interlocked.Read(ref totalDelayMilliseconds),
            Interlocked.Read(ref maxDelayMilliseconds));
    }

    private DevLocalImpairmentDecision BuildDelayDecision(
        DevLocalImpairmentLane lane,
        long currentSequence,
        string transferId,
        string frameType,
        string chunkIndex,
        int payloadBytes,
        int delayMs,
        bool reordered,
        string reason)
    {
        var boundedDelayMs = Math.Clamp(delayMs, 1, 500);
        return new DevLocalImpairmentDecision(
            lane,
            Options.Profile,
            currentSequence,
            Drop: false,
            Delay: TimeSpan.FromMilliseconds(boundedDelayMs),
            Reordered: reordered,
            Reason: reason,
            TransferId: transferId,
            FrameType: frameType,
            ChunkIndex: chunkIndex,
            PayloadBytes: payloadBytes);
    }

    private DevLocalImpairmentDecision NoopDecision(
        DevLocalImpairmentLane lane,
        long currentSequence,
        string transferId,
        string frameType,
        string chunkIndex,
        int payloadBytes)
        => new(
            lane,
            Options.Profile,
            currentSequence,
            Drop: false,
            Delay: TimeSpan.Zero,
            Reordered: false,
            Reason: "(none)",
            TransferId: transferId,
            FrameType: frameType,
            ChunkIndex: chunkIndex,
            PayloadBytes: payloadBytes);

    private bool ShouldDropLossBurstFrame(FileTransferDataFrame frame, string transferId, long currentSequence)
    {
        var chunkIndices = EnumerateFileTransferChunkIndices(frame).ToArray();
        if (chunkIndices.Length == 0)
        {
            return false;
        }

        lock (gate)
        {
            var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "(none)" : transferId;
            lossDropsByTransfer.TryGetValue(normalizedTransferId, out var dropsForTransfer);
            var shouldDropFirstEligible = dropsForTransfer == 0;
            var shouldDropPeriodic = StableModulo(currentSequence + chunkIndices[0], 17) == 0;
            if (!shouldDropFirstEligible && !shouldDropPeriodic)
            {
                return false;
            }

            var keys = chunkIndices
                .Select(index => BuildChunkKey(normalizedTransferId, index))
                .ToArray();
            if (keys.All(droppedFileTransferChunkKeys.Contains))
            {
                return false;
            }

            foreach (var key in keys)
            {
                droppedFileTransferChunkKeys.Add(key);
            }

            lossDropsByTransfer[normalizedTransferId] = dropsForTransfer + 1;
            return true;
        }
    }

    private void RecordAndLogDecision(DevLocalImpairmentDecision decision)
    {
        if (decision.Drop)
        {
            if (decision.Lane == DevLocalImpairmentLane.FileTransferData)
            {
                Interlocked.Increment(ref fileTransferDataFramesDropped);
            }
            else
            {
                Interlocked.Increment(ref screenShareMediaFramesDropped);
            }

            LocalOperationalLog.Warn(
                "DevLocalTransport",
                $"event=devlocal_impairment_frame_dropped; lane={FormatLane(decision.Lane)}; profile={decision.Profile}; transfer_id={decision.TransferId}; frame_type={decision.FrameType}; chunk_index={decision.ChunkIndex}; payload_bytes={decision.PayloadBytes}; sequence={decision.Sequence}; reason={decision.Reason}");
            return;
        }

        if (decision.Delay <= TimeSpan.Zero)
        {
            return;
        }

        var delayMs = (long)Math.Ceiling(decision.Delay.TotalMilliseconds);
        if (decision.Lane == DevLocalImpairmentLane.FileTransferData)
        {
            Interlocked.Increment(ref fileTransferDataFramesDelayed);
            if (decision.Reordered)
            {
                Interlocked.Increment(ref fileTransferDataFramesReordered);
            }
        }
        else
        {
            Interlocked.Increment(ref screenShareMediaFramesDelayed);
        }

        Interlocked.Add(ref totalDelayMilliseconds, delayMs);
        UpdateMaxDelay(delayMs);
        LocalOperationalLog.Info(
            "DevLocalTransport",
            $"event=devlocal_impairment_frame_delayed; lane={FormatLane(decision.Lane)}; profile={decision.Profile}; transfer_id={decision.TransferId}; frame_type={decision.FrameType}; chunk_index={decision.ChunkIndex}; payload_bytes={decision.PayloadBytes}; delay_ms={delayMs.ToString(CultureInfo.InvariantCulture)}; reordered={(decision.Reordered ? 1 : 0)}; sequence={decision.Sequence}; reason={decision.Reason}");
    }

    private void UpdateMaxDelay(long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref maxDelayMilliseconds);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxDelayMilliseconds, candidate, current) == current)
            {
                return;
            }
        }
    }

    private int StableModulo(long value, int modulo)
    {
        var mixed = unchecked(value * 1_103_515_245L + Options.Seed * 2_654_435_761L + 12_345L);
        return (int)(((mixed % modulo) + modulo) % modulo);
    }

    private bool IsReorderBurstCandidate(long currentSequence, FileTransferDataFrame frame)
    {
        var chunkIndices = EnumerateFileTransferChunkIndices(frame).ToArray();
        if (chunkIndices.Length == 0)
        {
            return false;
        }

        var transferId = string.IsNullOrWhiteSpace(frame.TransferId) ? "(none)" : frame.TransferId.Trim();
        lock (gate)
        {
            reorderDelaysByTransfer.TryGetValue(transferId, out var delayedForTransfer);
            if (delayedForTransfer == 0)
            {
                reorderDelaysByTransfer[transferId] = 1;
                return true;
            }

            if (currentSequence % 4 == 1 || StableModulo(currentSequence + chunkIndices[0], 9) == 0)
            {
                reorderDelaysByTransfer[transferId] = delayedForTransfer + 1;
                return true;
            }
        }

        return false;
    }

    private static bool IsFileTransferChunkPayload(FileTransferDataFrame frame)
        => frame is FileTransferChunkBatchFrameV4;

    private static int EstimateFileTransferPayloadBytes(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => batch.DataSegments.Sum(static segment => segment.Length),
            _ => 0,
        };

    private static string FormatFileTransferChunkIndex(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => FormatBatchChunkRange(batch),
            _ => "(none)",
        };

    private static string FormatBatchChunkRange(FileTransferChunkBatchFrameV4 batch)
    {
        var segmentCount = batch.DataSegments.Count;
        return segmentCount <= 0
            ? batch.StartChunkIndex.ToString(CultureInfo.InvariantCulture)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{batch.StartChunkIndex}-{batch.StartChunkIndex + segmentCount - 1}");
    }

    private static IEnumerable<int> EnumerateFileTransferChunkIndices(FileTransferDataFrame frame)
    {
        switch (frame)
        {
            case FileTransferChunkBatchFrameV4 batch:
                for (var offset = 0; offset < batch.DataSegments.Count; offset++)
                {
                    yield return batch.StartChunkIndex + offset;
                }
                break;
        }
    }

    private static string BuildChunkKey(string transferId, int chunkIndex)
        => transferId + ":" + chunkIndex.ToString(CultureInfo.InvariantCulture);

    private static string FormatLane(DevLocalImpairmentLane lane)
        => lane switch
        {
            DevLocalImpairmentLane.FileTransferData => "filetransfer_data",
            DevLocalImpairmentLane.ScreenShareMedia => "screenshare_media",
            _ => lane.ToString(),
        };
}
