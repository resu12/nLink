using System.Collections.Concurrent;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

internal sealed class FakeNknAccelerationLane : INknAccelerationLane
{
    private readonly bool sendResult;
    private bool disposed;
    private long controlAccepted;
    private long mediaAccepted;
    private long bulkAccepted;
    private long controlWritten;
    private long mediaWritten;
    private long bulkWritten;
    private long controlReceived;
    private long mediaReceived;
    private long bulkReceived;
    private long sendRejected;

    public FakeNknAccelerationLane(bool isAvailable = true, bool sendResult = true)
    {
        IsAvailable = isAvailable;
        this.sendResult = sendResult;
    }

    public bool IsAvailable { get; set; }

    public ConcurrentQueue<(NknBridgeChannel Lane, byte[] Payload)> Sent { get; } = new();

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

    public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot()
        => new(
            IsAvailable,
            IsAvailable ? string.Empty : "unavailable",
            Volatile.Read(ref controlAccepted),
            Volatile.Read(ref mediaAccepted),
            Volatile.Read(ref bulkAccepted),
            Volatile.Read(ref controlWritten),
            Volatile.Read(ref mediaWritten),
            Volatile.Read(ref bulkWritten),
            Volatile.Read(ref controlReceived),
            Volatile.Read(ref mediaReceived),
            Volatile.Read(ref bulkReceived),
            Volatile.Read(ref sendRejected),
            QueueOverflow: 0);

    public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
    {
        if (disposed || ct.IsCancellationRequested || !IsAvailable || !sendResult)
        {
            Interlocked.Increment(ref sendRejected);
            return Task.FromResult(false);
        }

        Sent.Enqueue((lane, envelopeBytes.AsSpan().ToArray()));
        IncrementLane(lane, ref controlAccepted, ref mediaAccepted, ref bulkAccepted);
        IncrementLane(lane, ref controlWritten, ref mediaWritten, ref bulkWritten);
        return Task.FromResult(true);
    }

    public void SetAvailable(bool isAvailable, string reason = "test")
    {
        IsAvailable = isAvailable;
        StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(isAvailable, reason));
    }

    public void InjectInbound(NknBridgeChannel lane, byte[] payload)
    {
        IncrementLane(lane, ref controlReceived, ref mediaReceived, ref bulkReceived);
        MessageReceived?.Invoke(
            this,
            new NknIncomingMessage(
                string.Empty,
                payload.AsSpan().ToArray(),
                isTopic: false,
                topic: null,
                channel: lane,
                bridgeIngressObservedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void Dispose()
    {
        disposed = true;
        IsAvailable = false;
    }

    private static void IncrementLane(NknBridgeChannel lane, ref long control, ref long media, ref long bulk)
    {
        switch (lane)
        {
            case NknBridgeChannel.Media:
                Interlocked.Increment(ref media);
                break;
            case NknBridgeChannel.Bulk:
                Interlocked.Increment(ref bulk);
                break;
            default:
                Interlocked.Increment(ref control);
                break;
        }
    }
}
