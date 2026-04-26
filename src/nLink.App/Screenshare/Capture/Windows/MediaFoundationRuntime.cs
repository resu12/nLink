using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal static class MediaFoundationRuntime
{
    private const int MfVersion = 0x00020070;
    private const int MfStartupFull = 0;
    private static readonly object Sync = new();
    private static int refCount;

    public static bool TryAcquire()
    {
        lock (Sync)
        {
            if (refCount == 0)
            {
                var hr = MFStartup(MfVersion, MfStartupFull);
                if (hr < 0)
                {
                    return false;
                }
            }

            refCount++;
            return true;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (refCount <= 0)
            {
                return;
            }

            refCount--;
            if (refCount == 0)
            {
                try
                {
                    MFShutdown();
                }
                catch
                {
                }
            }
        }
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}
