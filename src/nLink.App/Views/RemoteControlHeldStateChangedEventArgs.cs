using System;
using NLink.Core.RemoteControl;

namespace NLink.App.Views;

public sealed class RemoteControlHeldStateChangedEventArgs : EventArgs
{
    public RemoteControlHeldStateChangedEventArgs(
        RemoteControlModifiersMask modifiersMask,
        RemoteControlMouseButtonsMask mouseButtonsMask,
        bool immediateReleaseAll)
    {
        ModifiersMask = modifiersMask;
        MouseButtonsMask = mouseButtonsMask;
        ImmediateReleaseAll = immediateReleaseAll;
    }

    public RemoteControlModifiersMask ModifiersMask { get; }

    public RemoteControlMouseButtonsMask MouseButtonsMask { get; }

    public bool ImmediateReleaseAll { get; }
}

