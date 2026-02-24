using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace NLink.App.Threading;

internal static class UiThreadDispatch
{
    public static Task RunAsync(Action action)
    {
        if (Application.Current is null)
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}

