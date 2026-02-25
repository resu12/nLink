using System.Runtime.InteropServices;
using Xunit;

namespace NLink.SmokeTests;

internal sealed class GuiSmokeFactAttribute : FactAttribute
{
    public GuiSmokeFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "GUI smoke runs only on Windows.";
            return;
        }

        var value = Environment.GetEnvironmentVariable("NLINK_RUN_GUI_SMOKE");
        var enabled = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            Skip = "Set NLINK_RUN_GUI_SMOKE=1 to run GUI smoke tests.";
        }
    }
}

