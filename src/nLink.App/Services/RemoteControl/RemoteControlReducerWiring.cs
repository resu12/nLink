using System;
using NLink.Core.RemoteControl;

namespace NLink.App.Services.RemoteControl;

internal static class RemoteControlReducerWiring
{
    public static RemoteControlReducerResult Reduce(
        RemoteControlSessionState current,
        in RemoteControlReducerEvent evt)
    {
        return RemoteControlReducer.Apply(current, evt);
    }

    public static void ExecuteSideEffects(
        in RemoteControlReducerResult result,
        Action<RemoteControlSideEffect> executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        var effects = result.SideEffects;
        for (var i = 0; i < effects.Count; i++)
        {
            executor(effects[i]);
        }
    }
}
