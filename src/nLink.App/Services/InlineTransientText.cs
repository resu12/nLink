using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NLink.App.Threading;

namespace NLink.App.Services;

public sealed class InlineTransientText : ObservableObject, IDisposable
{
    private readonly object gate = new();
    private readonly ITimer timer;
    private readonly bool ownsTimer;
    private readonly TimeSpan defaultDuration;
    private int generation;
    private bool disposed;
    private bool isVisible;
    private string text = string.Empty;

    public InlineTransientText(ITimer? timer = null, TimeSpan? defaultDuration = null)
    {
        this.timer = timer ?? new ThreadPoolTimerAdapter();
        ownsTimer = timer is null;
        this.defaultDuration = defaultDuration ?? TimeSpan.FromSeconds(2);
    }

    public bool IsVisible
    {
        get => isVisible;
        private set => SetProperty(ref isVisible, value);
    }

    public string Text
    {
        get => text;
        private set => SetProperty(ref text, value);
    }

    public void Show(string message, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Hide();
            return;
        }

        int token;
        var hideAfter = duration ?? defaultDuration;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            token = ++generation;
            timer.Stop();
            timer.Start(hideAfter, Timeout.InfiniteTimeSpan, () => OnTimerElapsed(token));
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (disposed)
            {
                return;
            }

            Text = message;
            IsVisible = true;
        });
    }

    public void Hide()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            generation++;
            timer.Stop();
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (disposed)
            {
                return;
            }

            IsVisible = false;
            Text = string.Empty;
        });
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
            timer.Stop();
            if (ownsTimer)
            {
                timer.Dispose();
            }
        }
    }

    private void OnTimerElapsed(int token)
    {
        lock (gate)
        {
            if (disposed || token != generation)
            {
                return;
            }

            generation++;
            timer.Stop();
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (disposed)
            {
                return;
            }

            IsVisible = false;
            Text = string.Empty;
        });
    }
}
