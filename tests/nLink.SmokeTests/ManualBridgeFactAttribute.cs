using System.Runtime.InteropServices;

namespace NLink.SmokeTests;

internal sealed class ManualBridgeFactAttribute : FactAttribute
{
    public ManualBridgeFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Manual bridge tests run only on Windows.";
            return;
        }

        var value = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var enabled = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            Skip = "Set NLINK_RUN_MANUAL_BRIDGE=1 to run manual real-bridge tests.";
        }
    }
}
