namespace NLink.Infra.Nkn;

internal sealed class BridgeSubmissionPriorityGate
{
    private readonly object gate = new();
    private readonly Queue<Waiter> priorityWaiters = new();
    private readonly Queue<Waiter> normalWaiters = new();
    private bool held;

    internal (bool Held, int PriorityWaiters, int NormalWaiters) GetStateForTests()
    {
        lock (gate)
        {
            return (
                held,
                priorityWaiters.Count(static waiter => waiter.IsQueued),
                normalWaiters.Count(static waiter => waiter.IsQueued));
        }
    }

    internal Task<Lease> EnterAsync(bool priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (gate)
        {
            if (!held)
            {
                held = true;
                return Task.FromResult(new Lease(this));
            }

            waiter = new Waiter(this, ct);
            (priority ? priorityWaiters : normalWaiters).Enqueue(waiter);
        }

        waiter.RegisterCancellation();
        return waiter.Completion.Task;
    }

    private void Cancel(Waiter waiter)
    {
        lock (gate)
        {
            if (!waiter.IsQueued)
            {
                return;
            }

            waiter.IsQueued = false;
        }

        waiter.Completion.TrySetCanceled(waiter.CancellationToken);
    }

    private void Release()
    {
        Waiter? next;
        lock (gate)
        {
            next = DequeueNextLocked(priorityWaiters) ??
                DequeueNextLocked(normalWaiters);
            if (next is null)
            {
                held = false;
                return;
            }

            next.IsQueued = false;
        }

        next.CancellationRegistration.Dispose();
        next.Completion.TrySetResult(new Lease(this));
    }

    private static Waiter? DequeueNextLocked(Queue<Waiter> waiters)
    {
        while (waiters.Count > 0)
        {
            var candidate = waiters.Dequeue();
            if (candidate.IsQueued)
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class Waiter(
        BridgeSubmissionPriorityGate owner,
        CancellationToken cancellationToken)
    {
        private readonly BridgeSubmissionPriorityGate owner = owner;

        internal TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal CancellationTokenRegistration CancellationRegistration { get; set; }

        internal bool IsQueued { get; set; } = true;

        internal void RegisterCancellation()
        {
            var registration = CancellationToken.Register(
                static state =>
                {
                    var waiter = (Waiter)state!;
                    waiter.owner.Cancel(waiter);
                },
                this);
            var disposeRegistration = false;
            lock (owner.gate)
            {
                CancellationRegistration = registration;
                disposeRegistration = !IsQueued;
            }

            if (disposeRegistration)
            {
                registration.Dispose();
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private BridgeSubmissionPriorityGate? owner;

        internal Lease(BridgeSubmissionPriorityGate owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Release();
        }
    }
}
