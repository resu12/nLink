using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NLink.Core.Logging;

namespace NLink.App.Services.RemoteControl;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRemoteInputInjector : IRemoteInputInjector
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x1000;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const uint KeyEventKeyUp = 0x0002;

    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;

    private readonly IWindowsSendInputApi sendInputApi;

    public WindowsRemoteInputInjector()
        : this(new Win32WindowsSendInputApi())
    {
    }

    internal WindowsRemoteInputInjector(IWindowsSendInputApi sendInputApi)
    {
        this.sendInputApi = sendInputApi ?? throw new ArgumentNullException(nameof(sendInputApi));
    }

    public bool IsSupported => true;

    public void InjectMouseMoveAbsolute(int xPx, int yPx)
    {
        if (!RemoteDesktopMetrics.TryGetVirtualDesktopBounds(out var bounds))
        {
            LocalOperationalLog.Warn("RemoteControl", "event=input_inject_failed; reason=virtual_screen_metrics_invalid");
            return;
        }

        var absoluteX = WindowsRemoteInputMath.PixelToAbsoluteCoordinate(xPx, bounds.Left, bounds.Width);
        var absoluteY = WindowsRemoteInputMath.PixelToAbsoluteCoordinate(yPx, bounds.Top, bounds.Height);

        SendMouseInput(
            dx: absoluteX,
            dy: absoluteY,
            data: 0,
            flags: MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk);
    }

    public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
    {
        if (!TryMapMouseButton(button, action, out var flags, out var data))
        {
            return;
        }

        SendMouseInput(dx: 0, dy: 0, data, flags);
    }

    public void InjectMouseWheel(int deltaX, int deltaY)
    {
        // Protocol wheel values are notch units (same semantic as Avalonia PointerWheelEventArgs).
        // TODO(v0.5.0-P7): normalize wheel units across platforms/devices and carry
        // richer source metadata so scrolling behavior is consistent.
        if (deltaY != 0)
        {
            SendMouseInput(
                dx: 0,
                dy: 0,
                data: WindowsRemoteInputMath.ScaleWheelDelta(deltaY),
                flags: MouseEventWheel);
        }

        if (deltaX != 0)
        {
            SendMouseInput(
                dx: 0,
                dy: 0,
                data: WindowsRemoteInputMath.ScaleWheelDelta(deltaX),
                flags: MouseEventHWheel);
        }
    }

    public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
    {
        _ = mods; // Modifier state is carried as a hint; modifier keys are injected from explicit key events.
        // TODO(v0.5.0-P7): expand key mapping coverage and define IME/text-input strategy
        // (dead keys, composition, layout-dependent keys, and locale handling).
        if (!WindowsRemoteKeyMapper.TryMapVirtualKey(key, out var virtualKey))
        {
            return;
        }

        var flags = action == RemoteKeyAction.Up ? KeyEventKeyUp : 0u;
        SendKeyboardInput(virtualKey, flags);
    }

    private static bool TryMapMouseButton(
        RemoteMouseButton button,
        RemoteButtonAction action,
        out uint flags,
        out int data)
    {
        flags = 0u;
        data = 0;

        switch (button)
        {
            case RemoteMouseButton.Left:
                flags = action == RemoteButtonAction.Down ? MouseEventLeftDown : MouseEventLeftUp;
                return true;
            case RemoteMouseButton.Right:
                flags = action == RemoteButtonAction.Down ? MouseEventRightDown : MouseEventRightUp;
                return true;
            case RemoteMouseButton.Middle:
                flags = action == RemoteButtonAction.Down ? MouseEventMiddleDown : MouseEventMiddleUp;
                return true;
            case RemoteMouseButton.X1:
                flags = action == RemoteButtonAction.Down ? MouseEventXDown : MouseEventXUp;
                data = XButton1;
                return true;
            case RemoteMouseButton.X2:
                flags = action == RemoteButtonAction.Down ? MouseEventXDown : MouseEventXUp;
                data = XButton2;
                return true;
            default:
                return false;
        }
    }

    private void SendMouseInput(int dx, int dy, int data, uint flags)
    {
        var input = new WindowsInput
        {
            Type = InputMouse,
            Data = new WindowsInputUnion
            {
                Mouse = new WindowsMouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero,
                },
            },
        };

        SendInputInternal(input);
    }

    private void SendKeyboardInput(ushort virtualKey, uint flags)
    {
        var input = new WindowsInput
        {
            Type = InputKeyboard,
            Data = new WindowsInputUnion
            {
                Keyboard = new WindowsKeybdInput
                {
                    Vk = virtualKey,
                    Scan = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero,
                },
            },
        };

        SendInputInternal(input);
    }

    private void SendInputInternal(WindowsInput input)
    {
        var sent = sendInputApi.SendInput(1u, new[] { input }, Marshal.SizeOf<WindowsInput>());
        if (sent == 0u)
        {
            LocalOperationalLog.Warn(
                "RemoteControl",
                $"event=input_inject_failed; reason=send_input_error_{sendInputApi.GetLastError()}");
        }
    }
}

internal static class WindowsRemoteInputMath
{
    private const int WheelDelta = 120;

    public static int PixelToAbsoluteCoordinate(int pixelValue, int origin, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        var min = (long)origin;
        var max = min + length - 1L;
        var clamped = Math.Clamp((long)pixelValue, min, max);
        var relative = clamped - min;
        var absolute = (int)Math.Round(relative * 65535d / (length - 1d));
        return Math.Clamp(absolute, 0, 65535);
    }

    public static int ScaleWheelDelta(int protocolDelta)
    {
        var scaled = (long)protocolDelta * WheelDelta;
        if (scaled > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (scaled < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)scaled;
    }
}

internal static class WindowsRemoteKeyMapper
{
    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Back"] = 0x08,
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Space"] = 0x20,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Shift"] = 0x10,
        ["LeftShift"] = 0xA0,
        ["RightShift"] = 0xA1,
        ["Ctrl"] = 0x11,
        ["Control"] = 0x11,
        ["LeftCtrl"] = 0xA2,
        ["RightCtrl"] = 0xA3,
        ["Alt"] = 0x12,
        ["LeftAlt"] = 0xA4,
        ["RightAlt"] = 0xA5,
        ["Meta"] = 0x5B,
        ["Win"] = 0x5B,
        ["LWin"] = 0x5B,
        ["RWin"] = 0x5C,
    };

    public static bool TryMapVirtualKey(RemoteKey key, out ushort virtualKey)
    {
        virtualKey = 0;
        var logical = key.LogicalKey?.Trim();
        if (string.IsNullOrWhiteSpace(logical))
        {
            return false;
        }

        if (KeyMap.TryGetValue(logical, out virtualKey))
        {
            return true;
        }

        if (TryMapAlpha(logical, out virtualKey) ||
            TryMapDigit(logical, out virtualKey) ||
            TryMapFunction(logical, out virtualKey) ||
            TryMapNumpadDigit(logical, out virtualKey))
        {
            return true;
        }

        return false;
    }

    private static bool TryMapAlpha(string logical, out ushort virtualKey)
    {
        virtualKey = 0;
        if (logical.Length != 1)
        {
            return false;
        }

        var ch = char.ToUpperInvariant(logical[0]);
        if (ch is < 'A' or > 'Z')
        {
            return false;
        }

        virtualKey = (ushort)ch;
        return true;
    }

    private static bool TryMapDigit(string logical, out ushort virtualKey)
    {
        virtualKey = 0;
        if (logical.Length != 2 ||
            logical[0] != 'D' ||
            logical[1] is < '0' or > '9')
        {
            return false;
        }

        virtualKey = (ushort)logical[1];
        return true;
    }

    private static bool TryMapFunction(string logical, out ushort virtualKey)
    {
        virtualKey = 0;
        if (logical.Length < 2 ||
            logical[0] != 'F' ||
            !int.TryParse(logical[1..], out var fn) ||
            fn < 1 ||
            fn > 12)
        {
            return false;
        }

        virtualKey = (ushort)(0x70 + (fn - 1));
        return true;
    }

    private static bool TryMapNumpadDigit(string logical, out ushort virtualKey)
    {
        virtualKey = 0;
        const string prefix = "NumPad";
        if (!logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = logical[prefix.Length..];
        if (suffix.Length != 1 || suffix[0] is < '0' or > '9')
        {
            return false;
        }

        virtualKey = (ushort)(0x60 + (suffix[0] - '0'));
        return true;
    }
}

internal interface IWindowsSendInputApi
{
    uint SendInput(uint nInputs, WindowsInput[] pInputs, int cbSize);

    int GetLastError();
}

[SupportedOSPlatform("windows")]
internal sealed class Win32WindowsSendInputApi : IWindowsSendInputApi
{
    public uint SendInput(uint nInputs, WindowsInput[] pInputs, int cbSize) =>
        SendInputNative(nInputs, pInputs, cbSize);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(uint nInputs, [In] WindowsInput[] pInputs, int cbSize);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsInput
{
    public int Type;
    public WindowsInputUnion Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct WindowsInputUnion
{
    [FieldOffset(0)]
    public WindowsMouseInput Mouse;

    [FieldOffset(0)]
    public WindowsKeybdInput Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsMouseInput
{
    public int Dx;
    public int Dy;
    public int MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsKeybdInput
{
    public ushort Vk;
    public ushort Scan;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}
