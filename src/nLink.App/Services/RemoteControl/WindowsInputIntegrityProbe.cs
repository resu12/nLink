using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NLink.App.Services.RemoteControl;

internal static class WindowsInputIntegrityProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TryGetProcessElevation(GetCurrentProcess(), closeProcessHandle: false, out var elevated) && elevated;
    }

    public static bool TryIsForegroundWindowElevated(
        out bool isElevated,
        out uint processId,
        out string? processName)
    {
        isElevated = false;
        processId = 0;
        processName = null;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out processId);
        if (processId == 0)
        {
            return false;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!TryGetProcessElevation(processHandle, closeProcessHandle: true, out isElevated))
        {
            return false;
        }

        if (!isElevated)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            processName = null;
        }

        return true;
    }

    private static bool TryGetProcessElevation(IntPtr processHandle, bool closeProcessHandle, out bool isElevated)
    {
        isElevated = false;
        var tokenHandle = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle) || tokenHandle == IntPtr.Zero)
            {
                return false;
            }

            var tokenElevation = default(TokenElevation);
            var tokenElevationSize = Marshal.SizeOf<TokenElevation>();
            if (!GetTokenInformation(
                    tokenHandle,
                    TokenElevationInformationClass,
                    out tokenElevation,
                    tokenElevationSize,
                    out _))
            {
                return false;
            }

            isElevated = tokenElevation.TokenIsElevated != 0;
            return true;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                _ = CloseHandle(tokenHandle);
            }

            if (closeProcessHandle && processHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processHandle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
