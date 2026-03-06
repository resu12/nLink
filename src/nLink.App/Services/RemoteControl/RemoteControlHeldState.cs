using NLink.Core.RemoteControl;

namespace NLink.App.Services.RemoteControl;

internal sealed class RemoteControlHeldState
{
    public RemoteControlModifiersMask Modifiers { get; private set; } = RemoteControlModifiersMask.None;

    public RemoteControlMouseButtonsMask Buttons { get; private set; } = RemoteControlMouseButtonsMask.None;

    public void UpdateModifiers(RemoteControlModifiersMask modifiers)
    {
        Modifiers = modifiers;
    }

    public void ApplyMouseButton(RemoteControlMouseButtonsMask button, bool isDown)
    {
        if (button == RemoteControlMouseButtonsMask.None)
        {
            return;
        }

        Buttons = isDown ? Buttons | button : Buttons & ~button;
    }

    public void Clear()
    {
        Modifiers = RemoteControlModifiersMask.None;
        Buttons = RemoteControlMouseButtonsMask.None;
    }
}

