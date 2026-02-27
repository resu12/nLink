using System;
using System.Threading;

namespace NLink.App.Services;

public interface ITimer : IDisposable
{
    void Start(TimeSpan dueTime, TimeSpan period, Action callback);
    void Stop();
}

internal sealed class ThreadPoolTimerAdapter : ITimer
{
    private readonly object gate = new();
    private Timer? timer;
    private bool disposed;

    public void Start(TimeSpan dueTime, TimeSpan period, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            timer?.Dispose();
            timer = new Timer(
                _ =>
                {
                    try
                    {
                        callback();
                    }
                    catch
                    {
                        // Presenter guarantees no-throw behavior; swallow as a last-resort guard.
                    }
                },
                null,
                dueTime,
                period);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }
}

