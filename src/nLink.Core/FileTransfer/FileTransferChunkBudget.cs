namespace NLink.Core.FileTransfer;

public static class FileTransferChunkBudget
{
    public const int MaxRawChunkBytes = 48 * 1024;
    public const int MaxRawBatchBytesV3 = 64 * 1024;
    public const int MaxSerializedChunkPayloadBytes = 50 * 1024;
    public const int MaxSerializedChunkBatchPayloadBytesV3 = 64 * 1024;

    public static int ClampRequestedRawChunkSize(int requestedMaxChunkSize)
        => Math.Min(requestedMaxChunkSize, MaxRawChunkBytes);

    public static int ComputeLargestFittingRawChunkSize(
        int requestedMaxChunkSize,
        Func<int, bool> fitsWithinBudget,
        string noFitMessage)
    {
        ArgumentNullException.ThrowIfNull(fitsWithinBudget);
        ArgumentException.ThrowIfNullOrWhiteSpace(noFitMessage);

        var high = ClampRequestedRawChunkSize(requestedMaxChunkSize);
        if (high <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMaxChunkSize), "Requested chunk size must be positive.");
        }

        var low = 1;
        var best = 0;
        while (low <= high)
        {
            var candidate = low + ((high - low) / 2);
            if (fitsWithinBudget(candidate))
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (best <= 0)
        {
            throw new InvalidOperationException(noFitMessage);
        }

        return best;
    }
}
