using System.Security.Cryptography;
using NLink.Infra.Nkn;

namespace NLink.Tests;

// One opaque outbound frame, scheduling only. No decoding or route authority.
internal sealed class AttemptNknFrameHold
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<(int ByteCount, string Sha256)> selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int claimed, pendingBytes;

    internal Task<(int ByteCount, string Sha256)> Selected => selected.Task;
    internal int PendingBytes => Volatile.Read(ref pendingBytes);
    internal bool Release() => PendingBytes > 0 && release.TrySetResult();

    internal async Task<byte[]> BeforeSendAsync(byte kind, NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind > 1 || !Enum.IsDefined(lane) || bytes.Length is < 1 or > 1048576)
            throw new IOException("attempt_nkn_frame_invalid");
        // A large Regular bulk envelope is a candidate, not proof of a chunk.
        if (kind != 0 || lane != NknBridgeChannel.Bulk || bytes.Length < 4096 ||
            Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return bytes;
        var original = (byte[])bytes.Clone();
        Volatile.Write(ref pendingBytes, original.Length);
        selected.TrySetResult((original.Length, Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()));
        try
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            ct.ThrowIfCancellationRequested();
            return original;
        }
        finally { Volatile.Write(ref pendingBytes, 0); }
    }
}
