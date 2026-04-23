using System.Diagnostics;
using NLink.App.Services;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class NetworkResilienceCoordinatorTests
{
    [Fact]
    public void NetworkResilienceCoordinator_IgnoresNetworkDownEvents()
    {
        using var source = new FakeNetworkEventSource();
        using var timer = new FakeManualTimer();
        var callCount = 0;

        using var coordinator = new NetworkResilienceCoordinator(
            source,
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.CompletedTask;
            },
            debounceTimer: timer,
            debounceDelay: TimeSpan.FromMilliseconds(1));

        source.RaiseNetworkAvailabilityChanged(isAvailable: false);
        Assert.False(timer.IsRunning);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task NetworkResilienceCoordinator_DebouncesAndCoalescesSignals()
    {
        using var source = new FakeNetworkEventSource();
        using var timer = new FakeManualTimer();
        var calls = new List<ExternalRecoveryTrigger>();

        using var coordinator = new NetworkResilienceCoordinator(
            source,
            (triggers, _) =>
            {
                lock (calls)
                {
                    calls.Add(triggers);
                }

                return Task.CompletedTask;
            },
            debounceTimer: timer,
            debounceDelay: TimeSpan.FromMilliseconds(1));

        source.RaiseNetworkAvailabilityChanged(isAvailable: true);
        source.RaiseNetworkAddressChanged();
        source.RaiseResume();

        Assert.True(timer.IsRunning);
        timer.Tick();

        await WaitUntilAsync(() =>
        {
            lock (calls)
            {
                return calls.Count == 1;
            }
        });

        ExternalRecoveryTrigger trigger;
        lock (calls)
        {
            trigger = calls.Single();
        }

        Assert.True(trigger.HasFlag(ExternalRecoveryTrigger.NetworkAvailable));
        Assert.True(trigger.HasFlag(ExternalRecoveryTrigger.NetworkAddressChanged));
        Assert.True(trigger.HasFlag(ExternalRecoveryTrigger.Resume));
    }

    [Fact]
    public async Task NetworkResilienceCoordinator_DoesNotOverlapRecoveryCallbacks_AndQueuesFollowUp()
    {
        using var source = new FakeNetworkEventSource();
        using var timer = new FakeManualTimer();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var inflight = 0;
        var maxInflight = 0;

        using var coordinator = new NetworkResilienceCoordinator(
            source,
            async (_, _) =>
            {
                var current = Interlocked.Increment(ref inflight);
                var observedMax = Volatile.Read(ref maxInflight);
                while (current > observedMax)
                {
                    var previous = Interlocked.CompareExchange(ref maxInflight, current, observedMax);
                    if (previous == observedMax)
                    {
                        break;
                    }

                    observedMax = previous;
                }

                var sequence = Interlocked.Increment(ref callCount);
                if (sequence == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondStarted.TrySetResult();
                    await releaseSecond.Task;
                }

                Interlocked.Decrement(ref inflight);
            },
            debounceTimer: timer,
            debounceDelay: TimeSpan.FromMilliseconds(1));

        source.RaiseNetworkAvailabilityChanged(isAvailable: true);
        timer.Tick();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.RaiseNetworkAddressChanged();
        source.RaiseResume();
        var startCallsBeforeWhileInFlightTick = timer.StartCallCount;
        timer.Tick();

        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callCount));

        releaseFirst.TrySetResult();
        await WaitUntilAsync(() => Volatile.Read(ref inflight) == 0);
        await WaitUntilAsync(() => timer.StartCallCount > startCallsBeforeWhileInFlightTick);

        timer.Tick();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecond.TrySetResult();

        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 2 && Volatile.Read(ref inflight) == 0);
        Assert.Equal(1, Volatile.Read(ref maxInflight));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
    }

    private sealed class FakeNetworkEventSource : INetworkEventSource
    {
        public event EventHandler<NetworkAvailabilitySignalEventArgs>? NetworkAvailabilityChanged;
        public event EventHandler? NetworkAddressChanged;
        public event EventHandler? Resume;

        public void RaiseNetworkAvailabilityChanged(bool isAvailable)
            => NetworkAvailabilityChanged?.Invoke(this, new NetworkAvailabilitySignalEventArgs(isAvailable));

        public void RaiseNetworkAddressChanged()
            => NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseResume()
            => Resume?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private sealed class FakeManualTimer : NLink.App.Services.ITimer
    {
        private Action? callback;
        private bool disposed;

        public bool IsRunning { get; private set; }
        public int StartCallCount { get; private set; }

        public void Start(TimeSpan dueTime, TimeSpan period, Action callback)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
            IsRunning = true;
            StartCallCount++;
        }

        public void Stop()
        {
            IsRunning = false;
            callback = null;
        }

        public void Tick()
        {
            if (!IsRunning || callback is null || disposed)
            {
                return;
            }

            callback();
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }
    }
}
