using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

internal sealed class ScreenSharePolicyAwareTransportDouble :
    ScreenShareTransportBoundaryTestBase.ScreenShareAwareSignalingTransportDouble,
    IScreenShareTransportPolicyController
{
    public List<bool> PolicyUpdates { get; } = new();
    public List<string> QueueFlushReasons { get; } = new();

    public Task SetScreenShareTransportCatchUpOnlyAsync(bool active, CancellationToken ct)
    {
        PolicyUpdates.Add(active);
        return Task.CompletedTask;
    }

    public void FlushScreenShareTransportQueue(string reason)
    {
        QueueFlushReasons.Add(reason);
    }
}
