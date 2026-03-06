using System.Threading;
using NLink.Core.Logging;

namespace NLink.App.Services.RemoteControl;

internal sealed class NullRemoteInputInjector : IRemoteInputInjector
{
    private static int unsupportedLogged;

    public bool IsSupported => false;

    public void InjectMouseMoveAbsolute(int xPx, int yPx)
    {
        LogUnsupportedOnce();
    }

    public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
    {
        LogUnsupportedOnce();
    }

    public void InjectMouseWheel(int deltaX, int deltaY)
    {
        LogUnsupportedOnce();
    }

    public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
    {
        LogUnsupportedOnce();
    }

    private static void LogUnsupportedOnce()
    {
        if (Interlocked.CompareExchange(ref unsupportedLogged, 1, 0) != 0)
        {
            return;
        }

        LocalOperationalLog.Info("RemoteControl", "event=input_inject_ignored; reason=injector_not_supported");
    }
}
