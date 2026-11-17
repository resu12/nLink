using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using NLink.Infra.Nkn;

namespace NLink.Tests;

internal sealed record AttemptPipeWriteObservation(Guid WriteId, string Source, string Peer,
    byte Kind, NknBridgeChannel Lane, int Length, string Sha256, bool Completed, long Timestamp);

// Shared by the original pipe client and focused I/O tests. The caller retains
// its writer lock and fatal partial-frame cleanup; this helper never schedules.
internal static class AttemptPipeFrameWriter
{
    internal static async Task WriteAsync(Stream pipe, string source, string peer, byte kind,
        NknBridgeChannel lane, byte[] bytes, byte[]? hint, Action checkPeer,
        Action<AttemptPipeWriteObservation>? observe, CancellationToken ct)
    {
        var header = new byte[6]; header[0] = hint is null ? kind : (byte)2; header[1] = (byte)lane;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), bytes.Length);
        AttemptPipeWriteObservation? attempt = observe is null ? null : new(Guid.NewGuid(), source, peer,
            kind, lane, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), false, Stopwatch.GetTimestamp());
        if (attempt is not null) observe!(attempt);
        await pipe.WriteAsync(header, ct);
        if (hint is not null)
        {
            await pipe.WriteAsync(BitConverter.GetBytes(hint.Length), ct);
            await pipe.WriteAsync(hint, ct);
        }
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
        checkPeer();
        if (attempt is not null) observe!(attempt with { Completed = true, Timestamp = Stopwatch.GetTimestamp() });
    }
}
