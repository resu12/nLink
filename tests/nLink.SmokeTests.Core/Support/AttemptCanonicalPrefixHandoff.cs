using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using NLink.Tools;

namespace NLink.Tests;

// A single fixture-only scalar record. No payload, Core token or carrier frame.
internal sealed record AttemptCanonicalPrefixWitness(Guid SenderRequest, Guid ReceiverRequest, string Session, string Transfer)
{
    internal byte[] Encode()
    {
        if (SenderRequest == Guid.Empty || ReceiverRequest == Guid.Empty || SenderRequest == ReceiverRequest ||
            string.IsNullOrWhiteSpace(Session) || string.IsNullOrWhiteSpace(Transfer) || Session.Length > 128 || Transfer.Length > 128)
            throw new IOException("canonical_prefix_identity_invalid");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("nlink-canonical-prefix-v1");
            writer.Write(SenderRequest.ToByteArray()); writer.Write(ReceiverRequest.ToByteArray());
            writer.Write(Session); writer.Write(Transfer);
            writer.Write(AttemptCanonicalTailProfile.FileBytes);
            writer.Write(AttemptCanonicalTailProfile.ChunkBytes); writer.Write(AttemptCanonicalTailProfile.ChunkCount);
            writer.Write(AttemptCanonicalTailProfile.FirstChunk); // NextChunkIndex
            writer.Write(AttemptCanonicalTailProfile.FirstChunk); // ChunksTransferred
            writer.Write(AttemptCanonicalTailProfile.FirstChunk - 1); // Highest received
            writer.Write(AttemptCanonicalTailProfile.PrefixBytes); writer.Write(AttemptCanonicalTailProfile.PrefixBytes);
            writer.Write(0); writer.Write(0L); // Pending writes, buffered bytes
            writer.Write(AttemptCanonicalTailProfile.ChunkCount); // Bitmap shape, not committed bits
        }
        if (bytes.Length > 512) throw new IOException("canonical_prefix_body_capacity");
        return bytes.ToArray();
    }
}

internal sealed class AttemptCanonicalPrefixHandoff : IAsyncDisposable
{
    private readonly Stream pipe;
    private readonly AttemptCanonicalPrefixWitness expected;
    private readonly Action check;
    private readonly CancellationTokenSource lifetime;
    private readonly bool sender;
    private int published, disposed;
    internal Task Received { get; }

    // The stream seam is used by deterministic framing/lifetime tests only.
    // Native callers must use OpenAsync and the retained peer's endpoint check.
    internal AttemptCanonicalPrefixHandoff(Stream pipe, AttemptCanonicalPrefixWitness expected, bool sender,
        Action check, CancellationToken ct, Action? checkSetup = null)
    {
        this.pipe = pipe; this.expected = expected; this.sender = sender; this.check = check;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Received = sender ? ReceiveAsync(checkSetup ?? check) : Task.CompletedTask;
    }

    internal static async Task<AttemptCanonicalPrefixHandoff> OpenAsync(LocalFrontierAppRequest.AppLease lease,
        AttemptPeerProcessChannel peer, AttemptNknPipeClient carrier, AttemptCanonicalPrefixScope scope, bool sender, CancellationToken ct)
    {
        var host = sender ? lease.RequestId : peer.PeerRequest;
        var name = "nlink-canonical-prefix-" + host.ToString("N");
        PipeStream pipe = sender
            ? new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)
            : new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            if (pipe is NamedPipeServerStream listener) await listener.WaitForConnectionAsync(ct);
            else await ((NamedPipeClientStream)pipe).ConnectAsync(ct);
            if (!lease.IsCurrent || !peer.MatchesPipe(pipe.SafePipeHandle, sender)) throw new IOException("canonical_prefix_peer_mismatch");
            void CheckPeer()
            {
                if (!lease.IsCurrent || !peer.IsCurrent || !carrier.IsCurrent) throw new IOException("canonical_prefix_peer_retired");
            }
            void Check() { CheckPeer(); scope.Check(); }
            void CheckSetup() { CheckPeer(); scope.CheckSetup(); }
            CheckSetup();
            return new(pipe, new(host, sender ? peer.PeerRequest : lease.RequestId, scope.Session, scope.Transfer), sender, Check, ct, CheckSetup);
        }
        catch { pipe.Dispose(); throw; }
    }

    internal void Check() => Check(check);

    private void Check(Action checkCurrentness)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(AttemptCanonicalPrefixHandoff));
        lifetime.Token.ThrowIfCancellationRequested();
        checkCurrentness();
    }

    private async Task ReceiveAsync(Action checkSetup)
    {
        Check(checkSetup);
        await ReadRecordAsync(pipe, expected, lifetime.Token);
        // EOF intentionally closes the dedicated producer pipe. Recheck the
        // retained process and original carrier, not a reopened pipe or PID.
        Check();
    }

    internal async Task PublishAsync()
    {
        if (sender || Interlocked.Exchange(ref published, 1) != 0) throw new IOException("canonical_prefix_duplicate_publish");
        try
        {
            Check();
            var body = expected.Encode();
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await pipe.WriteAsync(header, lifetime.Token);
            await pipe.WriteAsync(body, lifetime.Token);
            await pipe.FlushAsync(lifetime.Token);
            Check();
            Console.WriteLine("carrier-canonical-prefix=exported; next=" + AttemptCanonicalTailProfile.FirstChunk +
                "; bytes=" + AttemptCanonicalTailProfile.PrefixBytes);
        }
        finally { pipe.Dispose(); } // EOF, no ack, retry or reconnect.
    }

    internal static async Task ReadRecordAsync(Stream pipe, AttemptCanonicalPrefixWitness expected, CancellationToken ct)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 512) throw new IOException("canonical_prefix_body_capacity");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        // Exact canonical encoding rejects malformed/foreign/schema/geometry
        // variants without allocating anything from untrusted length fields.
        if (!body.AsSpan().SequenceEqual(expected.Encode())) throw new IOException("canonical_prefix_record_mismatch");
        if (await pipe.ReadAsync(header.AsMemory(0, 1), ct) != 0) throw new IOException("canonical_prefix_trailing_input");
        ct.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); pipe.Dispose();
        try { await Received; }
        catch (Exception) { /* The pending consumer sees failure, never a successful cleanup release. */ }
        lifetime.Dispose();
    }
}
