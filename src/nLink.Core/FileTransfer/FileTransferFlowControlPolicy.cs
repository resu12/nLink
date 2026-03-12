namespace NLink.Core.FileTransfer;

public enum FileTransferFlowControlMode
{
    Interactive,
    InteractiveCritical,
    Background,
}

public readonly record struct FileTransferFlowControlPolicy(
    FileTransferFlowControlMode Mode,
    long TargetOutstandingBytes,
    long ReorderSlackBytes,
    int LocalInFlightChunkSends,
    int ChunkPacingMs,
    int MinExtensionStepChunks,
    int LowWatermarkChunks)
{
    public static FileTransferFlowControlPolicy InteractiveCriticalDefault { get; } = new(
        Mode: FileTransferFlowControlMode.InteractiveCritical,
        TargetOutstandingBytes: 384L * 1024,
        ReorderSlackBytes: 128L * 1024,
        LocalInFlightChunkSends: 4,
        ChunkPacingMs: 2,
        MinExtensionStepChunks: 8,
        LowWatermarkChunks: 12);

    public static FileTransferFlowControlPolicy InteractiveDefault { get; } = new(
        Mode: FileTransferFlowControlMode.Interactive,
        TargetOutstandingBytes: 1536L * 1024,
        ReorderSlackBytes: 1024L * 1024,
        LocalInFlightChunkSends: 8,
        ChunkPacingMs: 2,
        MinExtensionStepChunks: 32,
        LowWatermarkChunks: 32);

    public static FileTransferFlowControlPolicy BackgroundDefault { get; } = new(
        Mode: FileTransferFlowControlMode.Background,
        TargetOutstandingBytes: 4L * 1024 * 1024,
        ReorderSlackBytes: 2L * 1024 * 1024,
        LocalInFlightChunkSends: 12,
        ChunkPacingMs: 1,
        MinExtensionStepChunks: 32,
        LowWatermarkChunks: 32);

    public long HardOutstandingCapBytes => checked(TargetOutstandingBytes + ReorderSlackBytes);

    public static FileTransferFlowControlPolicy Normalize(FileTransferFlowControlPolicy policy)
    {
        var targetOutstandingBytes = policy.TargetOutstandingBytes <= 0
            ? InteractiveDefault.TargetOutstandingBytes
            : policy.TargetOutstandingBytes;
        var reorderSlackBytes = policy.ReorderSlackBytes < 0
            ? 0
            : policy.ReorderSlackBytes;
        var localInFlightChunkSends = policy.LocalInFlightChunkSends <= 0
            ? InteractiveDefault.LocalInFlightChunkSends
            : policy.LocalInFlightChunkSends;
        var chunkPacingMs = policy.ChunkPacingMs < 0 ? 0 : policy.ChunkPacingMs;
        var minExtensionStepChunks = policy.MinExtensionStepChunks <= 0
            ? InteractiveDefault.MinExtensionStepChunks
            : policy.MinExtensionStepChunks;
        var lowWatermarkChunks = policy.LowWatermarkChunks <= 0
            ? InteractiveDefault.LowWatermarkChunks
            : policy.LowWatermarkChunks;

        return policy with
        {
            TargetOutstandingBytes = targetOutstandingBytes,
            ReorderSlackBytes = reorderSlackBytes,
            LocalInFlightChunkSends = localInFlightChunkSends,
            ChunkPacingMs = chunkPacingMs,
            MinExtensionStepChunks = minExtensionStepChunks,
            LowWatermarkChunks = lowWatermarkChunks,
        };
    }
}

public interface IFileTransferFlowControlPolicyAwareTransport
{
    void SetFileTransferFlowControlPolicy(FileTransferFlowControlPolicy policy);
}
