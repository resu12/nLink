namespace NLink.Infra.Nkn;

internal sealed class ConnectAttemptCoordinator
{
    private readonly object gate = new();

    private TaskCompletionSource<string>? pendingReady;
    private string? pendingReadyConnectId;
    private Task? connectInFlightTask;
    private long connectInFlightSequence;

    public Task GetOrCreateConnectTask(bool bridgeRunning, Func<long, Task> createTask)
    {
        lock (gate)
        {
            if (bridgeRunning && pendingReady is not null && pendingReady.Task.IsCompletedSuccessfully)
            {
                return Task.CompletedTask;
            }

            if (connectInFlightTask is not null && !connectInFlightTask.IsCompleted)
            {
                return connectInFlightTask;
            }

            var sequence = ++connectInFlightSequence;
            var task = createTask(sequence);
            connectInFlightTask = task;
            return task;
        }
    }

    public TaskCompletionSource<string> RegisterPendingReady(string connectId)
    {
        lock (gate)
        {
            pendingReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingReadyConnectId = connectId;
            return pendingReady;
        }
    }

    public ConnectReadyAcceptResult AcceptReady(string resolvedAddress, bool hasConnectId, string? readyConnectId)
    {
        lock (gate)
        {
            if (pendingReady is null)
            {
                return ConnectReadyAcceptResult.NoPending(null);
            }

            var expected = pendingReadyConnectId;
            if (!string.IsNullOrWhiteSpace(expected))
            {
                if (hasConnectId)
                {
                    if (!string.Equals(expected, readyConnectId, System.StringComparison.Ordinal))
                    {
                        return ConnectReadyAcceptResult.StaleMismatch(expected, readyConnectId ?? string.Empty);
                    }
                }
                else
                {
                    pendingReady.TrySetResult(resolvedAddress);
                    return ConnectReadyAcceptResult.AcceptedMissingConnectId(expected);
                }
            }

            pendingReady.TrySetResult(resolvedAddress);
            return ConnectReadyAcceptResult.Accepted(expected);
        }
    }

    public bool WasConnected()
    {
        lock (gate)
        {
            return pendingReady is not null && pendingReady.Task.IsCompletedSuccessfully;
        }
    }

    public void FailPendingReady(string reason)
    {
        lock (gate)
        {
            pendingReady?.TrySetException(new System.InvalidOperationException(reason));
            pendingReadyConnectId = null;
        }
    }

    public void ResetPendingReadyForNewProcessStart()
    {
        lock (gate)
        {
            pendingReady = null;
            pendingReadyConnectId = null;
        }
    }

    public void CompleteAttempt(long sequence, string connectId)
    {
        lock (gate)
        {
            if (pendingReadyConnectId == connectId)
            {
                pendingReadyConnectId = null;
            }

            if (pendingReady is not null && pendingReady.Task.IsCompleted)
            {
                // Keep completed pendingReady to preserve "already connected" fast-path semantics.
            }
            else if (pendingReadyConnectId is null)
            {
                pendingReady = null;
            }

            if (connectInFlightSequence == sequence)
            {
                connectInFlightTask = null;
            }
        }
    }
}

internal readonly record struct ConnectReadyAcceptResult(
    ConnectReadyAcceptKind Kind,
    string? ExpectedConnectId,
    string? ActualConnectId)
{
    public static ConnectReadyAcceptResult NoPending(string? expectedConnectId) =>
        new(ConnectReadyAcceptKind.NoPending, expectedConnectId, null);

    public static ConnectReadyAcceptResult StaleMismatch(string? expectedConnectId, string? actualConnectId) =>
        new(ConnectReadyAcceptKind.StaleMismatch, expectedConnectId, actualConnectId);

    public static ConnectReadyAcceptResult Accepted(string? expectedConnectId) =>
        new(ConnectReadyAcceptKind.Accepted, expectedConnectId, null);

    public static ConnectReadyAcceptResult AcceptedMissingConnectId(string? expectedConnectId) =>
        new(ConnectReadyAcceptKind.AcceptedMissingConnectId, expectedConnectId, null);
}

internal enum ConnectReadyAcceptKind
{
    NoPending,
    StaleMismatch,
    Accepted,
    AcceptedMissingConnectId,
}
