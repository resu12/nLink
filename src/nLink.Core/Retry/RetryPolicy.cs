namespace NLink.Core.Retry;

public enum RetryEventKind
{
    AttemptStart,
    AttemptScheduled,
    AttemptSuccess,
    FinalFail,
}

public readonly record struct RetryEvent(
    RetryEventKind Kind,
    int Attempt,
    int MaxAttempts,
    TimeSpan? Delay,
    string Reason,
    string ExceptionType);

public sealed record RetryPolicyOptions(
    int MaxAttempts,
    TimeSpan InitialDelay,
    TimeSpan MaxDelay,
    double JitterRatio = 0d)
{
    public static RetryPolicyOptions Default { get; } = new(
        MaxAttempts: 3,
        InitialDelay: TimeSpan.FromMilliseconds(250),
        MaxDelay: TimeSpan.FromSeconds(2),
        JitterRatio: 0.10);
}

public sealed record RetryExecutionResult(
    bool Succeeded,
    int Attempts,
    Exception? LastException);

public sealed class RetryPolicy
{
    private readonly RetryPolicyOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Func<double> nextRandom;

    public RetryPolicy(
        RetryPolicyOptions options,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<double>? nextRandom = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAttempts));
        }

        if (options.InitialDelay < TimeSpan.Zero || options.MaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (double.IsNaN(options.JitterRatio) || double.IsInfinity(options.JitterRatio) || options.JitterRatio < 0d || options.JitterRatio > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options.JitterRatio));
        }

        this.options = options;
        this.delayAsync = delayAsync ?? Task.Delay;
        this.nextRandom = nextRandom ?? Random.Shared.NextDouble;
    }

    public event EventHandler<RetryEvent>? EventEmitted;

    public async Task<RetryExecutionResult> ExecuteAsync(
        Func<int, CancellationToken, Task> operationAsync,
        Func<int, CancellationToken, Task>? resetBetweenAttemptsAsync,
        CancellationToken ct,
        Func<Exception, bool>? shouldRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operationAsync);
        shouldRetry ??= static _ => true;

        Exception? last = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Emit(new RetryEvent(RetryEventKind.AttemptStart, attempt, options.MaxAttempts, null, string.Empty, string.Empty));

            try
            {
                await operationAsync(attempt, ct).ConfigureAwait(false);
                Emit(new RetryEvent(RetryEventKind.AttemptSuccess, attempt, options.MaxAttempts, null, string.Empty, string.Empty));
                return new RetryExecutionResult(true, attempt, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                var canRetry = attempt < options.MaxAttempts && shouldRetry(ex);
                if (!canRetry)
                {
                    Emit(new RetryEvent(RetryEventKind.FinalFail, attempt, options.MaxAttempts, null, ex.Message, ex.GetType().Name));
                    return new RetryExecutionResult(false, attempt, ex);
                }

                if (resetBetweenAttemptsAsync is not null)
                {
                    await resetBetweenAttemptsAsync(attempt, ct).ConfigureAwait(false);
                }

                var delay = ComputeDelay(attempt);
                Emit(new RetryEvent(RetryEventKind.AttemptScheduled, attempt, options.MaxAttempts, delay, ex.Message, ex.GetType().Name));
                await delayAsync(delay, ct).ConfigureAwait(false);
            }
        }

        Emit(new RetryEvent(RetryEventKind.FinalFail, options.MaxAttempts, options.MaxAttempts, null, last?.Message ?? "retry_failed", last?.GetType().Name ?? "(none)"));
        return new RetryExecutionResult(false, options.MaxAttempts, last);
    }

    internal TimeSpan ComputeDelayForTests(int attempt) => ComputeDelay(attempt);

    private TimeSpan ComputeDelay(int attempt)
    {
        var baseMs = options.InitialDelay.TotalMilliseconds * Math.Pow(2d, Math.Max(0, attempt - 1));
        baseMs = Math.Min(baseMs, options.MaxDelay.TotalMilliseconds);

        if (baseMs <= 0d || options.JitterRatio <= 0d)
        {
            return TimeSpan.FromMilliseconds(baseMs);
        }

        var jitterWindow = baseMs * options.JitterRatio;
        var random = Math.Clamp(nextRandom(), 0d, 1d);
        var signedOffset = (random * 2d - 1d) * jitterWindow;
        var jittered = Math.Clamp(baseMs + signedOffset, 0d, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(jittered);
    }

    private void Emit(RetryEvent evt)
    {
        EventEmitted?.Invoke(this, evt);
    }
}
