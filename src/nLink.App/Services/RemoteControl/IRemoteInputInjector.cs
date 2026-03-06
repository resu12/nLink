using System;

namespace NLink.App.Services.RemoteControl;

[Flags]
internal enum RemoteKeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3,
}

internal enum RemoteMouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

internal enum RemoteButtonAction
{
    Down,
    Up,
}

internal enum RemoteKeyAction
{
    Down,
    Up,
}

internal readonly record struct RemoteKey(string LogicalKey, string? PhysicalKey = null);

internal interface IRemoteInputInjector
{
    bool IsSupported { get; }

    void InjectMouseMoveAbsolute(int xPx, int yPx);

    void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action);

    // deltaX/deltaY are wheel-notch units from protocol; Windows implementation converts to WHEEL_DELTA multiples.
    void InjectMouseWheel(int deltaX, int deltaY);

    void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods);
}
