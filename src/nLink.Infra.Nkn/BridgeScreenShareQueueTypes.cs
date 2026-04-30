namespace NLink.Infra.Nkn;

internal enum BridgeScreenShareQueueMode
{
    Normal = 0,
    CatchUpOnly = 1,
}

internal readonly record struct BridgeScreenShareQueueState(
    int QueueDepth,
    int QueuedBytes,
    long OldestQueuedAgeMs,
    bool InFlight,
    long DroppedSinceLast,
    bool IsCongested,
    bool IsSevere,
    BridgeScreenShareQueueMode Mode);

internal readonly record struct BridgeBulkQueueState(
    int QueueDepth,
    long QueuedBytes,
    long OldestQueuedAgeMs,
    bool InFlight,
    int InFlightCount,
    long InFlightBytes,
    int ConfiguredConcurrency,
    int EffectiveConcurrency,
    long ClearedSinceLast,
    bool IsCongested,
    bool IsSevere);

internal readonly record struct BridgeScreenShareHealthState(
    long RecentIssueCount,
    bool IsSevere,
    long OldestIssueAgeMs);

internal sealed class BridgeScreenShareQueueStateChangedEventArgs : EventArgs
{
    public BridgeScreenShareQueueStateChangedEventArgs(BridgeScreenShareQueueState state)
    {
        State = state;
    }

    public BridgeScreenShareQueueState State { get; }
}

internal interface IBridgeScreenShareQueueCapability
{
    bool IsBridgeProcessRunning { get; }

    BridgeScreenShareQueueState CurrentScreenShareQueueState { get; }

    BridgeScreenShareHealthState CurrentScreenShareHealthState { get; }

    event EventHandler<BridgeScreenShareQueueStateChangedEventArgs>? ScreenShareQueueStateChanged;

    Task SetScreenSharePolicyAsync(BridgeScreenShareQueueMode mode, long generation, bool flushQueued, CancellationToken ct);
}
