using System;
using NLink.Core.Logging;

namespace NLink.App.Services.RemoteControl;

internal static class RemoteInputInjectorFactory
{
    public static IRemoteInputInjector CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new WindowsRemoteInputInjector();
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "RemoteControl",
                    $"event=input_injector_create_failed; reason={ex.GetType().Name}; fallback=noop");
            }
        }

        return new NullRemoteInputInjector();
    }
}
