using System.Collections.Concurrent;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

internal sealed class FakeNknAccelerationLane : INknAccelerationLane
{
    private readonly bool sendResult;
    private readonly bool enableFailureOrigins;
    private readonly object failureOriginGate = new();
    private readonly Guid laneInstanceId = Guid.NewGuid();
    private bool available;
    private long clientGeneration = 1;
    private long failureGeneration;
    private NknAccelerationFailureOrigin? currentFailureOrigin;
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

    public FakeNknAccelerationLane(bool isAvailable = true, bool sendResult = true, bool enableFailureOrigins = false)
    {
        available = isAvailable;
        this.sendResult = sendResult;
        this.enableFailureOrigins = enableFailureOrigins;
    }

    public bool IsAvailable
    {
        get { lock (failureOriginGate) { return available; } }
        set
        {
            lock (failureOriginGate)
            {
                if (enableFailureOrigins && value)
                {
                    if (!available) { clientGeneration++; }
                    currentFailureOrigin = null;
                }
                available = value;
            }
        }
    }

    public bool IsFailureOriginCurrent(NknAccelerationFailureOrigin origin)
    {
        lock (failureOriginGate) { return IsFailureOriginCurrentUnsafe(origin); }
    }

    public bool TryApplyFailureOrigin(NknAccelerationFailureOrigin origin, Func<bool> apply)
    {
        BeforeFailureOriginApply?.Invoke();
        lock (failureOriginGate) { return IsFailureOriginCurrentUnsafe(origin) && apply(); }
    }

    public Action? BeforeFailureOriginApply { get; set; }

    private bool IsFailureOriginCurrentUnsafe(NknAccelerationFailureOrigin origin)
        => enableFailureOrigins && !disposed && !available && !origin.IsDefault && currentFailureOrigin == origin;

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
            QueueOverflow: 0,
            SequenceGap: 0,
            SequenceReordered: 0,
            TerminalSidecarReason: string.Empty,
            FallbackEpoch: 0);

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
        NknAccelerationFailureOrigin? origin;
        lock (failureOriginGate)
        {
            IsAvailable = isAvailable;
            origin = enableFailureOrigins && !disposed && !isAvailable
                ? new NknAccelerationFailureOrigin(laneInstanceId, clientGeneration,
                    $"fake-lane-run-{clientGeneration}", ++failureGeneration)
                : null;
            currentFailureOrigin = origin;
        }
        StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(isAvailable, reason, origin));
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
        lock (failureOriginGate)
        {
            disposed = true;
            available = false;
            currentFailureOrigin = null;
        }
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
