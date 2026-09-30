using System.Buffers.Binary;
using System.IO.Pipes;
using NLink.Infra.Nkn;
using NLink.Tools;

namespace NLink.Tests;

// Offline byte carrier only. NknSignalingTransport and Core still own the
// session, route, runtime and fallback protocol; pipe delivery grants none.
internal sealed class AttemptNknPipeClient : INknClient, IAuthoritativeConnectedAddressSource,
    IRuntimeUnlockRetryPriorityNknClient, IAsyncDisposable
{
    private readonly AttemptPeerProcessChannel peer;
    private readonly PipeStream pipe;
    private readonly bool server;
    private readonly string remote;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object deliveries = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? reader;
    private bool connected, disposed;
    internal Action<NknBridgeChannel, byte[]>? TunaReceived;
    internal AttemptNknFrameHold? OutboundHold;
    internal AttemptOfflineFrameDescriptors? OutboundDescriptors;
    internal AttemptOfflineFrameSchedule? InboundSchedule;
    internal Action<AttemptPipeWriteObservation>? WriteObserver { get; set; }

    private AttemptNknPipeClient(AttemptPeerProcessChannel peer, PipeStream pipe, bool server, string address, string remote)
    { this.peer = peer; this.pipe = pipe; this.server = server; Address = address; this.remote = remote; }

    internal static async Task<AttemptNknPipeClient> OpenAsync(LocalFrontierAppRequest.AppLease lease,
        AttemptPeerProcessChannel peer, bool server, CancellationToken ct)
    {
        var host = server ? lease.RequestId : peer.PeerRequest;
        var name = "nlink-attempt-nkn-" + host.ToString("N");
        PipeStream pipe = server
            ? new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)
            : new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            if (pipe is NamedPipeServerStream listener) await listener.WaitForConnectionAsync(ct);
            else await ((NamedPipeClientStream)pipe).ConnectAsync(ct);
            if (!lease.IsCurrent || !peer.MatchesPipe(pipe.SafePipeHandle, server)) throw new IOException("attempt_nkn_peer_mismatch");
            return new(peer, pipe, server, "attempt." + lease.RequestId.ToString("N"), "attempt." + peer.PeerRequest.ToString("N"));
        }
        catch { pipe.Dispose(); throw; }
    }

    public string Address { get; }
    public string MediaAddress => LaneAddress(Address, NknBridgeChannel.Media);
    public string BulkAddress => LaneAddress(Address, NknBridgeChannel.Bulk);
    internal string PeerAddress => remote;
    internal bool IsCurrent => !disposed && pipe.IsConnected && peer.MatchesPipe(pipe.SafePipeHandle, server);
    bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => connected && IsCurrent;
    public event EventHandler<NknIncomingMessage>? MessageReceived;
    public event EventHandler? Disconnected;

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Check();
        if (!connected) { connected = true; reader = ReadAsync(lifetime.Token); }
        return Task.CompletedTask;
    }
    public Task DisconnectAsync() { Dispose(); return Task.CompletedTask; }
    public Task SubscribeAsync(string topic, CancellationToken ct) => throw new NotSupportedException("direct_invite_only");
    public Task UnsubscribeAsync(string topic) => Task.CompletedTask;
    public Task PublishAsync(string topic, byte[] payload, CancellationToken ct) => throw new NotSupportedException("direct_invite_only");
    public Task SendAsync(string destination, byte[] payload, CancellationToken ct) => SendRegularAsync(destination, payload, NknBridgeChannel.Control, ct);
    public Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct) => SendRegularAsync(destination, payload, NknBridgeChannel.Media, ct);
    public Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct) => SendRegularAsync(destination, payload, NknBridgeChannel.Bulk, ct);
    public Task SendRuntimeUnlockRetryControlAsync(string destination, byte[] payload, NknBridgeChannel channel, CancellationToken ct)
        => SendRegularAsync(destination, payload, channel, ct);

    private Task SendRegularAsync(string destination, byte[] bytes, NknBridgeChannel lane, CancellationToken ct)
    {
        if (destination != remote && destination != LaneAddress(remote, NknBridgeChannel.Media) &&
            destination != LaneAddress(remote, NknBridgeChannel.Bulk)) throw new IOException("attempt_nkn_foreign_destination");
        return SendFrameAsync(0, lane, bytes, ct);
    }
    internal async Task<bool> SendTunaAsync(NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
    { await SendFrameAsync(1, lane, bytes, ct); return true; }

    private async Task SendFrameAsync(byte kind, NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is < 1 or > 1048576 || !Enum.IsDefined(lane)) throw new IOException("attempt_nkn_frame_invalid");
        if (OutboundHold is { } hold)
        {
            Check(); if (!connected) throw new IOException("attempt_nkn_not_connected");
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            bytes = await hold.BeforeSendAsync(kind, lane, bytes, pending.Token);
        }
        await writes.WaitAsync(ct);
        try
        {
            Check(); if (!connected) throw new IOException("attempt_nkn_not_connected");
            var hint = OutboundDescriptors?.Consume(kind, lane, bytes);
            try
            {
                await AttemptPipeFrameWriter.WriteAsync(pipe, Address, remote, kind, lane, bytes,
                    hint, Check, WriteObserver, ct);
            }
            catch { Dispose(); throw; } // A partial frame cannot be reused as a new frame boundary.
        }
        finally { writes.Release(); }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var header = new byte[6];
        while (true)
        {
            await pipe.ReadExactlyAsync(header, ct); Check();
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
            var lane = (NknBridgeChannel)header[1];
            if (header[0] > 2 || length is < 1 or > 1048576 || !Enum.IsDefined(lane) ||
                (header[0] == 2 && (InboundSchedule is null || lane != NknBridgeChannel.Bulk)))
                throw new IOException("attempt_nkn_frame_invalid");
            AttemptOfflineFrameHint? hint = null;
            if (header[0] == 2)
            {
                var prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, ct);
                var hintLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (hintLength is < 1 or > 1024) throw new IOException("attempt_nkn_hint_invalid");
                var hintBytes = new byte[hintLength]; await pipe.ReadExactlyAsync(hintBytes, ct);
                hint = AttemptOfflineFrameHint.Decode(hintBytes);
            }
            var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, ct); Check();
            var kind = header[0];
            if (hint is not null) InboundSchedule!.Receive(hint, bytes, original => Deliver(0, lane, original));
            else Deliver(kind, lane, bytes);
        }
    }
    private void Deliver(byte kind, NknBridgeChannel lane, byte[] bytes)
    {
        lock (deliveries)
        {
            Check(); lifetime.Token.ThrowIfCancellationRequested();
            if (kind == 1) TunaReceived!(lane, bytes);
            else MessageReceived?.Invoke(this, new NknIncomingMessage(LaneAddress(remote, lane), bytes, false, null, lane));
        }
    }
    private void Check() { if (!IsCurrent) throw new IOException("attempt_nkn_peer_retired"); }
    private static string LaneAddress(string address, NknBridgeChannel lane) => lane switch
    {
        NknBridgeChannel.Media => address.Replace("attempt.", "attempt-media.", StringComparison.Ordinal),
        NknBridgeChannel.Bulk => address.Replace("attempt.", "attempt-bulk.", StringComparison.Ordinal),
        _ => address,
    };
    internal void CheckReader()
    { if (reader?.IsCompleted == true) reader.GetAwaiter().GetResult(); Check(); OutboundDescriptors?.Check(); InboundSchedule?.Check(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; connected = false; lifetime.Cancel(); pipe.Dispose();
        InboundSchedule?.Dispose();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (reader is not null)
            try { await reader; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        lifetime.Dispose(); writes.Dispose();
    }
}
