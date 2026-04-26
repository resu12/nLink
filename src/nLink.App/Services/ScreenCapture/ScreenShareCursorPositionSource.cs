using System;
using System.Runtime.InteropServices;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareCursorPositionSnapshot(
    int X,
    int Y,
    bool Visible);

internal interface IScreenShareCursorPositionSource
{
    bool TryGetCursorPosition(out ScreenShareCursorPositionSnapshot snapshot);
}

internal sealed class WindowsScreenShareCursorPositionSource : IScreenShareCursorPositionSource
{
    private const int CursorShowing = 0x00000001;

    public bool TryGetCursorPosition(out ScreenShareCursorPositionSnapshot snapshot)
    {
        snapshot = default;
        var info = new CursorInfo
        {
            Size = Marshal.SizeOf<CursorInfo>(),
        };

        if (!GetCursorInfo(ref info))
        {
            return false;
        }

        snapshot = new ScreenShareCursorPositionSnapshot(
            info.Point.X,
            info.Point.Y,
            (info.Flags & CursorShowing) == CursorShowing);
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
